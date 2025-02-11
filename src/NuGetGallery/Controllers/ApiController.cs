﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.UI;
using Newtonsoft.Json.Linq;
using NuGet;
using NuGetGallery.Authentication;
using NuGetGallery.Filters;
using NuGetGallery.Packaging;

namespace NuGetGallery
{
    public partial class ApiController : AppController
    {
        public IEntitiesContext EntitiesContext { get; set; }
        public INuGetExeDownloaderService NugetExeDownloaderService { get; set; }
        public IPackageFileService PackageFileService { get; set; }
        public IPackageService PackageService { get; set; }
        public IUserService UserService { get; set; }
        public IStatisticsService StatisticsService { get; set; }
        public IContentService ContentService { get; set; }
        public ISearchService SearchService { get; set; }
        public IIndexingService IndexingService { get; set; }
        public IAutomaticallyCuratePackageCommand AutoCuratePackage { get; set; }
        public IStatusService StatusService { get; set; }
        
        protected ApiController() { }

        public ApiController(
            IEntitiesContext entitiesContext,
            IPackageService packageService,
            IPackageFileService packageFileService,
            IUserService userService,
            INuGetExeDownloaderService nugetExeDownloaderService,
            IContentService contentService,
            IIndexingService indexingService,
            ISearchService searchService,
            IAutomaticallyCuratePackageCommand autoCuratePackage,
            IStatusService statusService)
        {
            EntitiesContext = entitiesContext;
            PackageService = packageService;
            PackageFileService = packageFileService;
            UserService = userService;
            NugetExeDownloaderService = nugetExeDownloaderService;
            ContentService = contentService;
            StatisticsService = null;
            IndexingService = indexingService;
            SearchService = searchService;
            AutoCuratePackage = autoCuratePackage;
            StatusService = statusService;
        }

        public ApiController(
            IEntitiesContext entitiesContext,
            IPackageService packageService,
            IPackageFileService packageFileService,
            IUserService userService,
            INuGetExeDownloaderService nugetExeDownloaderService,
            IContentService contentService,
            IIndexingService indexingService,
            ISearchService searchService,
            IAutomaticallyCuratePackageCommand autoCuratePackage,
            IStatusService statusService,
            IStatisticsService statisticsService)
            : this(entitiesContext, packageService, packageFileService, userService, nugetExeDownloaderService, contentService, indexingService, searchService, autoCuratePackage, statusService)
        {
            StatisticsService = statisticsService;
        }

        [HttpGet]
        [ActionName("GetPackageApi")]
        public virtual async Task<ActionResult> GetPackage(string id, string version)
        {
            // some security paranoia about URL hacking somehow creating e.g. open redirects
            // validate user input: explicit calls to the same validators used during Package Registrations
            // Ideally shouldn't be necessary?
            if (!PackageIdValidator.IsValidPackageId(id ?? ""))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, "The format of the package id is invalid");
            }

            if (!String.IsNullOrEmpty(version))
            {
                SemanticVersion dummy;
                if (!SemanticVersion.TryParse(version, out dummy))
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, "The package version is not a valid semantic version");
                }
            }

            // Normalize the version
            version = SemanticVersionExtensions.Normalize(version);

            // if the version is null, the user is asking for the latest version. Presumably they don't want includePrerelease release versions. 
            // The allow prerelease flag is ignored if both partialId and version are specified.
            // In general we want to try to add download statistics for any package regardless of whether a version was specified.

            // Only allow database requests to take up to 5 seconds.  Any longer and we just lose the data; oh well.
            EntitiesContext.SetCommandTimeout(5);

            Package package = null;
            try
            {
                package = PackageService.FindPackageByIdAndVersion(id, version, allowPrerelease: false);
                if (package == null)
                {
                    return new HttpStatusCodeWithBodyResult(
                        HttpStatusCode.NotFound, String.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));
                }

                try
                {
                    var stats = new PackageStatistics
                    {
                        IPAddress = Request.UserHostAddress,
                        UserAgent = Request.UserAgent,
                        Package = package,
                        Operation = Request.Headers["NuGet-Operation"],
                        DependentPackage = Request.Headers["NuGet-DependentPackage"],
                        ProjectGuids = Request.Headers["NuGet-ProjectGuids"],
                    };

                    PackageService.AddDownloadStatistics(stats);
                }
                catch (ReadOnlyModeException)
                {
                    // *gulp* Swallowed. It's OK not to add statistics and ok to not log errors in read only mode.
                }
                catch (SqlException e)
                {
                    // Log the error and continue
                    QuietLog.LogHandledException(e);
                }
                catch (DataException e)
                {
                    // Log the error and continue
                    QuietLog.LogHandledException(e);
                }
            }
            catch (SqlException e)
            {
                QuietLog.LogHandledException(e);
            }
            catch (DataException e)
            {
                QuietLog.LogHandledException(e);
            }
            
            // Fall back to constructing the URL based on the package version and ID.
            if (String.IsNullOrEmpty(version) && package == null)
            {
                // Database was unavailable and we don't have a version, return a 503
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.ServiceUnavailable, Strings.DatabaseUnavailable_TrySpecificVersion);
            }
            return await PackageFileService.CreateDownloadPackageActionResultAsync(
                HttpContext.Request.Url, 
                id, 
                String.IsNullOrEmpty(version) ? package.NormalizedVersion : version);
        }

        [HttpGet]
        [ActionName("GetNuGetExeApi")]
        [OutputCache(VaryByParam = "none", Location = OutputCacheLocation.ServerAndClient, Duration = 600)]
        public virtual Task<ActionResult> GetNuGetExe()
        {
            return NugetExeDownloaderService.CreateNuGetExeDownloadActionResultAsync(HttpContext.Request.Url);
        }

        [HttpGet]
        [ActionName("StatusApi")]
        public async virtual Task<ActionResult> Status()
        {
            if (StatusService == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.ServiceUnavailable, "Status service is unavailable");
            }
            return await StatusService.GetStatus();
        }

        [HttpGet]
        [RequireSsl]
        [ApiAuthorize]
        [ActionName("VerifyPackageKeyApi")]
        public virtual ActionResult VerifyPackageKey(string id, string version)
        {
            if (!String.IsNullOrEmpty(id))
            {
                // If the partialId is present, then verify that the user has permission to push for the specific Id \ version combination.
                var package = PackageService.FindPackageByIdAndVersion(id, version);
                if (package == null)
                {
                    return new HttpStatusCodeWithBodyResult(
                        HttpStatusCode.NotFound, String.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));
                }

                var user = GetCurrentUser();
                if (!package.IsOwner(user))
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, Strings.ApiKeyNotAuthorized);
                }
            }

            return new EmptyResult();
        }

        [HttpPut]
        [RequireSsl]
        [ApiAuthorize]
        [ActionName("PushPackageApi")]
        public virtual Task<ActionResult> CreatePackagePut()
        {
            return CreatePackageInternal();
        }

        [HttpPost]
        [RequireSsl]
        [ApiAuthorize]
        [ActionName("PushPackageApi")]
        public virtual Task<ActionResult> CreatePackagePost()
        {
            return CreatePackageInternal();
        }

        private async Task<ActionResult> CreatePackageInternal()
        {
            // Get the user
            var user = GetCurrentUser();

            using (var packageToPush = ReadPackageFromRequest())
            {
                if (packageToPush.Metadata.MinClientVersion > typeof(Manifest).Assembly.GetName().Version)
                {
                    return new HttpStatusCodeWithBodyResult(HttpStatusCode.BadRequest, String.Format(
                        CultureInfo.CurrentCulture,
                        Strings.UploadPackage_MinClientVersionOutOfRange,
                        packageToPush.Metadata.MinClientVersion));
                }

                // Ensure that the user can push packages for this partialId.
                var packageRegistration = PackageService.FindPackageRegistrationById(packageToPush.Metadata.Id);
                if (packageRegistration != null)
                {
                    if (!packageRegistration.IsOwner(user))
                    {
                        return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, Strings.ApiKeyNotAuthorized);
                    }

                    // Check if a particular Id-Version combination already exists. We eventually need to remove this check.
                    string normalizedVersion = packageToPush.Metadata.Version.ToNormalizedString();
                    bool packageExists =
                        packageRegistration.Packages.Any(
                            p => String.Equals(
                                p.NormalizedVersion,
                                normalizedVersion,
                                StringComparison.OrdinalIgnoreCase));

                    if (packageExists)
                    {
                        return new HttpStatusCodeWithBodyResult(
                            HttpStatusCode.Conflict,
                            String.Format(CultureInfo.CurrentCulture, Strings.PackageExistsAndCannotBeModified,
                                          packageToPush.Metadata.Id, packageToPush.Metadata.Version.ToNormalizedStringSafe()));
                    }
                }

                var package = PackageService.CreatePackage(packageToPush, user, commitChanges: false);
                AutoCuratePackage.Execute(package, packageToPush, commitChanges: false);
                EntitiesContext.SaveChanges();

                using (Stream uploadStream = packageToPush.GetStream())
                {
                    await PackageFileService.SavePackageFileAsync(package, uploadStream);
                }
            }

            return new HttpStatusCodeResult(HttpStatusCode.Created);
        }

        [HttpDelete]
        [RequireSsl]
        [ApiAuthorize]
        [ActionName("DeletePackageApi")]
        public virtual ActionResult DeletePackage(string id, string version)
        {
            var package = PackageService.FindPackageByIdAndVersion(id, version);
            if (package == null)
            {
                return new HttpStatusCodeWithBodyResult(
                    HttpStatusCode.NotFound, String.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));
            }

            var user = GetCurrentUser();
            if (!package.IsOwner(user))
            {
                return new HttpStatusCodeWithBodyResult(
                    HttpStatusCode.Forbidden, Strings.ApiKeyNotAuthorized);
            }

            PackageService.MarkPackageUnlisted(package);
            IndexingService.UpdatePackage(package);
            return new EmptyResult();
        }

        [HttpPost]
        [RequireSsl]
        [ApiAuthorize]
        [ActionName("PublishPackageApi")]
        public virtual ActionResult PublishPackage(string id, string version)
        {
            var package = PackageService.FindPackageByIdAndVersion(id, version);
            if (package == null)
            {
                return new HttpStatusCodeWithBodyResult(
                    HttpStatusCode.NotFound, String.Format(CultureInfo.CurrentCulture, Strings.PackageWithIdAndVersionNotFound, id, version));
            }

            User user = GetCurrentUser();
            if (!package.IsOwner(user))
            {
                return new HttpStatusCodeWithBodyResult(HttpStatusCode.Forbidden, String.Format(CultureInfo.CurrentCulture, Strings.ApiKeyNotAuthorized, "publish"));
            }

            PackageService.MarkPackageListed(package);
            IndexingService.UpdatePackage(package);
            return new EmptyResult();
        }

        public virtual async Task<ActionResult> ServiceAlert()
        {
            var alert = await ContentService.GetContentItemAsync(Constants.ContentNames.Alert, TimeSpan.Zero);
            return Content(alert == null ? (string)null : alert.ToString(), "text/html");
        }

        protected override void OnException(ExceptionContext filterContext)
        {
            var exception = filterContext.Exception;
            if (exception is ReadOnlyModeException)
            {
                filterContext.ExceptionHandled = true;
                filterContext.Result = new HttpStatusCodeWithBodyResult(
                    HttpStatusCode.ServiceUnavailable, exception.Message);
            }
            else
            {
                var request = filterContext.HttpContext.Request;
                filterContext.ExceptionHandled = true;
                filterContext.Result = new HttpStatusCodeWithBodyResult(
                    HttpStatusCode.InternalServerError, exception.Message, request.IsLocal ? exception.StackTrace : exception.Message);
            }
        }

        protected internal virtual INupkg ReadPackageFromRequest()
        {
            Stream stream;
            if (Request.Files.Count > 0)
            {
                // If we're using the newer API, the package stream is sent as a file.
                stream = Request.Files[0].InputStream;
            }
            else
            {
                stream = Request.InputStream;
            }

            return new Nupkg(stream, leaveOpen: false);
        }

        [HttpGet]
        [ActionName("PackageIDs")]
        public virtual ActionResult GetPackageIds(string partialId, bool? includePrerelease)
        {
            var query = GetService<IPackageIdsQuery>();
            return new JsonResult
                {
                    Data = (query.Execute(partialId, includePrerelease).ToArray()),
                    JsonRequestBehavior = JsonRequestBehavior.AllowGet
                };
        }

        [HttpGet]
        [ActionName("PackageVersions")]
        public virtual ActionResult GetPackageVersions(string id, bool? includePrerelease)
        {
            var query = GetService<IPackageVersionsQuery>();
            return new JsonResult
                {
                    Data = query.Execute(id, includePrerelease).ToArray(),
                    JsonRequestBehavior = JsonRequestBehavior.AllowGet
                };
        }

        [HttpGet]
        [ActionName("StatisticsDownloadsApi")]
        public virtual async Task<ActionResult> GetStatsDownloads(int? count)
        {
            var result = await StatisticsService.LoadDownloadPackageVersions();

            if (result.Loaded)
            {
                int i = 0;

                JArray content = new JArray();
                foreach (StatisticsPackagesItemViewModel row in StatisticsService.DownloadPackageVersionsAll)
                {
                    JObject item = new JObject();

                    item.Add("PackageId", row.PackageId);
                    item.Add("PackageVersion", row.PackageVersion);
                    item.Add("Gallery", Url.PackageGallery(row.PackageId, row.PackageVersion));
                    item.Add("PackageTitle", row.PackageTitle ?? row.PackageId);
                    item.Add("PackageDescription", row.PackageDescription);
                    item.Add("PackageIconUrl", row.PackageIconUrl ?? Url.PackageDefaultIcon());
                    item.Add("Downloads", row.Downloads);

                    content.Add(item);

                    i++;

                    if (count.HasValue && count.Value == i)
                    {
                        break;
                    }
                }

                return new ContentResult
                {
                    Content = content.ToString(),
                    ContentType = "application/json"
                };
            }

            return new HttpStatusCodeResult(HttpStatusCode.NotFound);
        }
    }
}
