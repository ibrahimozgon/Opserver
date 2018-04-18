﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using StackExchange.Opserver.Data.Exceptions;
using StackExchange.Opserver.Helpers;
using StackExchange.Opserver.Models;
using StackExchange.Opserver.Views.Exceptions;
using System.Threading.Tasks;
using StackExchange.Opserver.Data.Jira;

namespace StackExchange.Opserver.Controllers
{
    [OnlyAllow(Roles.Exceptions)]
    public class ExceptionsController : StatusController
    {
        public const int MaxSearchResults = 2000;

        public override ISecurableModule SettingsModule => Current.Settings.Exceptions;

        public override TopTab TopTab => new TopTab("Exceptions", nameof(Exceptions), this, 50)
        {
            GetMonitorStatus = () => ExceptionsModule.MonitorStatus,
            GetBadgeCount = () => ExceptionsModule.TotalExceptionCount,
            GetTooltip = () => ExceptionsModule.TotalRecentExceptionCount.ToComma() + " recent"
        };

        private List<ApplicationGroup> ApplicationGroups => CurrentStore.ApplicationGroups;
        private ExceptionStore CurrentStore;
        private string CurrentGroup;
        private string CurrentLog;
        private ExceptionSorts CurrentSort;

        protected override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            CurrentStore = ExceptionsModule.GetStore(Request.Params["store"]);
            CurrentGroup = Request.Params["group"];
            CurrentLog = Request.Params["log"] ?? Request.Params["app"]; // old link compat
            Enum.TryParse(Request.Params["sort"], out CurrentSort);

            if (CurrentLog.HasValue())
            {
                var storeApps = CurrentStore.Applications.Data;
                var a = storeApps?.Find(app => app.Name == CurrentLog) ?? storeApps?.Find(app => app.ShortName == CurrentLog);
                if (a != null)
                {
                    // Correct the log name to a found one, this enables short names to work.
                    CurrentLog = a.Name;
                    // Make pre-group links work correctly
                    if (CurrentGroup.IsNullOrEmpty())
                    {
                        // Old links, that didn't know about groups
                        var g = ApplicationGroups.Find(gr => gr[a.Name] != null);
                        if (g != null)
                        {
                            CurrentGroup = g.Name;
                        }
                    }
                }
            }

            Response.Cache.SetCacheability(HttpCacheability.NoCache);
            Response.Cache.SetNoStore();
            base.OnActionExecuting(filterContext);
        }

        private ExceptionStore.SearchParams GetSearch()
        {
            return new ExceptionStore.SearchParams
            {
                Group = CurrentGroup,
                Log = CurrentLog,
                Sort = CurrentSort
            };
        }

        private ExceptionsModel GetModel(List<Exceptional.Error> errors)
        {
            var group = CurrentGroup.HasValue() ? CurrentStore.ApplicationGroups.Find(g => g.Name == CurrentGroup) : null;
            var log = group != null && CurrentLog.HasValue() ? group.Applications.Find(a => a.Name == CurrentLog) : null;
            // Handle single-log groups a bit more intuitively so things like clear all work
            if (log == null && group?.Applications.Count == 1)
            {
                log = group.Applications[0];
            }
            return new ExceptionsModel
            {
                Store = CurrentStore,
                Groups = ApplicationGroups,
                Group = group,
                Log = log,
                Sort = CurrentSort,
                Errors = errors
            };
        }

        [Route("exceptions")]
        public async Task<ActionResult> Exceptions(string q, bool showDeleted = false)
        {
            var search = GetSearch();
            search.SearchQuery = q;
            search.IncludeDeleted = showDeleted;

            var errors = await CurrentStore.GetErrorsAsync(search).ConfigureAwait(false);

            var vd = GetModel(errors);
            vd.Search = q;
            vd.LoadAsyncSize = Current.Settings.Exceptions.PageSize;
            return View(vd);
        }

        [Route("exceptions/load-more")]
        public async Task<ActionResult> LoadMore(string q, bool showDeleted = false, int? count = null, Guid? prevLast = null)
        {
            var search = GetSearch();
            search.SearchQuery = q;
            search.Count = count ?? Current.Settings.Exceptions.PageSize;
            search.StartAt = prevLast;
            search.IncludeDeleted = showDeleted;

            var errors = await CurrentStore.GetErrorsAsync(search).ConfigureAwait(false);
            var vd = GetModel(errors);
            vd.Search = q;
            return View("Exceptions.Table.Rows", vd);
        }

        [Route("exceptions/similar")]
        public async Task<ActionResult> Similar(string id, bool byTime = false, int rangeInSeconds = 5 * 60)
        {
            var e = await CurrentStore.GetErrorAsync(CurrentLog, id).ConfigureAwait(false);
            if (e == null)
                return View("Exceptions.Detail", null);

            var search = GetSearch();
            if (byTime)
            {
                search.StartDate = e.CreationDate.AddMinutes(-rangeInSeconds);
                search.EndDate = e.CreationDate.AddMinutes(rangeInSeconds);
            }
            else
            {
                search.Message = e.Message;
            }

            var errors = await CurrentStore.GetErrorsAsync(search).ConfigureAwait(false);

            var vd = GetModel(errors);
            vd.Exception = e;
            vd.ClearLinkForVisibleOnly = true;
            return View("Exceptions.Similar", vd);
        }

        // TODO: Figure out a good "clear all" then redirect and remove this
        [Route("exceptions/search")]
        public async Task<ActionResult> Search(string q, bool showDeleted = false)
        {
            // empty searches go back to the main log
            if (q.IsNullOrEmpty())
                return RedirectToAction(nameof(Exceptions), new { group = CurrentGroup, log = CurrentLog });

            var search = GetSearch();
            search.SearchQuery = q;
            search.IncludeDeleted = showDeleted;
            search.Count = MaxSearchResults;

            var errors = await CurrentStore.GetErrorsAsync(search).ConfigureAwait(false);
            var vd = GetModel(errors);
            vd.Search = q;
            vd.ShowDeleted = showDeleted;
            vd.ClearLinkForVisibleOnly = true;
            return View("Exceptions.Search", vd);
        }

        [Route("exceptions/detail")]
        public async Task<ActionResult> Detail(string id)
        {
            var e = await CurrentStore.GetErrorAsync(CurrentLog, id).ConfigureAwait(false);
            var vd = GetModel(null);
            vd.Exception = e;
            return View("Exceptions.Detail", vd);
        }

        [Route("exceptions/preview")]
        public async Task<ActionResult> Preview(string id)
        {
            var e = await CurrentStore.GetErrorAsync(CurrentLog, id).ConfigureAwait(false);

            var vd = GetModel(null);
            vd.Exception = e;
            return PartialView("Exceptions.Preview", vd);
        }

        [Route("exceptions/detail/json"), AlsoAllow(Roles.Anonymous)]
        public async Task<JsonResult> DetailJson(string id)
        {
            var e = await CurrentStore.GetErrorAsync(CurrentLog, id).ConfigureAwait(false);
            if (e == null)
            {
                return JsonNotFound();
            }
            else
            {
                return Json(new
                {
                    e.GUID,
                    e.ErrorHash,
                    e.ApplicationName,
                    e.Type,
                    e.Source,
                    e.Message,
                    e.Detail,
                    e.MachineName,
                    e.Host,
                    e.FullUrl,
                    e.HTTPMethod,
                    e.IPAddress,
                    e.DuplicateCount,
                    CreationDate = e.CreationDate.ToEpochTime(),
                    e.Commands,
                });
            }
        }

        [Route("exceptions/protect"), HttpPost, AcceptVerbs(HttpVerbs.Post), OnlyAllow(Roles.ExceptionsAdmin)]
        public async Task<ActionResult> Protect(Guid id, bool redirect = false)
        {
            var success = await CurrentStore.ProtectErrorAsync(id).ConfigureAwait(false);
            if (!success) JsonError("Unable to protect, error was not found in the log");
            return redirect ? Json(new { url = Url.Action(nameof(Exceptions), new { store = CurrentStore.Name, group = CurrentGroup, log = CurrentLog }) }) : Counts();
        }

        [Route("exceptions/delete"), HttpPost, AcceptVerbs(HttpVerbs.Post), OnlyAllow(Roles.ExceptionsAdmin)]
        public async Task<ActionResult> Delete(Guid id, bool redirect = false)
        {
            // we don't care about success...if it's *already* deleted, that's fine
            // if we throw an exception trying to delete, that's another matter
            await CurrentStore.DeleteErrorAsync(id).ConfigureAwait(false);

            return redirect ? Json(new { url = Url.Action(nameof(Exceptions), new { store = CurrentStore.Name, group = CurrentGroup, log = CurrentLog }) }) : Counts();
        }

        [Route("exceptions/delete-all"), HttpPost, AcceptVerbs(HttpVerbs.Post), OnlyAllow(Roles.ExceptionsAdmin)]
        public async Task<ActionResult> DeleteAll()
        {
            await CurrentStore.DeleteAllErrorsAsync(new List<string> { CurrentLog }).ConfigureAwait(false);

            return Json(new { url = Url.Action("Exceptions", new { store = CurrentStore.Name, group = CurrentGroup }) });
        }

        [Route("exceptions/delete-similar"), AcceptVerbs(HttpVerbs.Post), OnlyAllow(Roles.ExceptionsAdmin)]
        public async Task<ActionResult> DeleteSimilar(string id)
        {
            var e = await CurrentStore.GetErrorAsync(CurrentLog, id).ConfigureAwait(false);
            await CurrentStore.DeleteSimilarErrorsAsync(e).ConfigureAwait(false);

            return Json(true);
        }

        [Route("exceptions/delete-list"), AcceptVerbs(HttpVerbs.Post), OnlyAllow(Roles.ExceptionsAdmin)]
        public async Task<ActionResult> DeleteList(Guid[] ids, bool returnCounts = false)
        {
            if (ids == null || ids.Length == 0) return Json(true);
            await CurrentStore.DeleteErrorsAsync(ids.ToList()).ConfigureAwait(false);

            return returnCounts ? Counts() : Json(new { url = Url.Action("Exceptions", new { store = CurrentStore.Name, log = CurrentLog, group = CurrentGroup }) });
        }

        [Route("exceptions/counts")]
        public ActionResult Counts()
        {
            var stores = ExceptionsModule.Stores.Select(s => new
            {
                s.Name,
                Total = s.TotalExceptionCount
            });
            var groups = ApplicationGroups.Select(g => new
            {
                g.Name,
                g.Total,
                Applications = g.Applications.Select(a => new
                {
                    a.Name,
                    Total = a.ExceptionCount
                })
            });
            return Json(new
            {
                Stores = stores,
                Groups = groups,
                Total = ExceptionsModule.TotalExceptionCount
            });
        }

        [Route("exceptions/jiraactions"), AcceptVerbs(HttpVerbs.Get), OnlyAllow(Roles.ExceptionsAdmin)]
        public ActionResult JiraActions(string appName)
        {
            var issues = Current.Settings.Jira.GetActionsForApplication(appName);
            return View("Exceptions.Jira", issues);
        }

        [Route("exceptions/jiraaction"), AcceptVerbs(HttpVerbs.Post), OnlyAllow(Roles.ExceptionsAdmin)]
        public async Task<ActionResult> JiraAction(string id, int actionid)
        {
            var e = await CurrentStore.GetErrorAsync(CurrentLog, id).ConfigureAwait(false);
            var action = Current.Settings.Jira.Actions.Find(i => i.Id == actionid);
            var jiraClient = new JiraClient(Current.Settings.Jira);
            var result = await jiraClient.CreateIssueAsync(action, e, "").ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(result.Key))
            {
                return Json(new
                {
                    success = false,
                    message = "Can not create issue"
                });
            }

            return Json(new
            {
                success = true,
                issueKey = result.Key,
                browseUrl = result.BrowseUrl
            });
        }
    }
}
