﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Profiling;
using StackExchange.Exceptional;
using StackExchange.Opserver.Helpers;

namespace StackExchange.Opserver.Data.Exceptions
{
    public class ExceptionStore : PollNode
    {
        public const int PerAppSummaryCount = 1000;

        public override string ToString() => "Store: " + Settings.Name;

        private int? QueryTimeout => Settings.QueryTimeoutMs;
        public string Name => Settings.Name;
        public string Description => Settings.Description;
        public string TableName => Settings.TableName.IsNullOrEmptyReturn("Exceptions");
        public string ServiceTableName => Settings.ServiceTableName.IsNullOrEmptyReturn("[dbo].[ExtendedServiceLog]");
        public ExceptionsSettings.Store Settings { get; internal set; }

        public override int MinSecondsBetweenPolls => 1;
        public override string NodeType => "Exceptions";

        public override IEnumerable<Cache> DataPollers
        {
            get { yield return Applications; }
        }

        protected override IEnumerable<MonitorStatus> GetMonitorStatus()
        {
            yield return DataPollers.GetWorstStatus();
        }

        protected override string GetMonitorStatusReason() { return null; }

        public ExceptionStore(ExceptionsSettings.Store settings) : base(settings.Name)
        {
            Settings = settings;
            ApplicationGroups = GetConfiguredApplicationGroups();
            KnownApplications = ApplicationGroups.SelectMany(g => g.Applications.Select(a => a.Name)).ToHashSet();
        }

        public int TotalExceptionCount => Applications.Data?.Sum(a => a.ExceptionCount) ?? 0;
        public int TotalRecentExceptionCount => Applications.Data?.Sum(a => a.RecentExceptionCount) ?? 0;
        private ApplicationGroup CatchAll { get; set; }
        public List<ApplicationGroup> ApplicationGroups { get; private set; }
        public HashSet<string> KnownApplications { get; }

        private List<ApplicationGroup> GetConfiguredApplicationGroups()
        {
            var groups = Settings.Groups ?? Current.Settings.Exceptions.Groups;
            var applications = Settings.Applications ?? Current.Settings.Exceptions.Applications;
            if (groups?.Any() ?? false)
            {
                var configured = groups
                    .Select(g => new ApplicationGroup
                    {
                        Name = g.Name,
                        Applications = g.Applications.OrderBy(a => a).Select(a => new Application { Name = a }).ToList()
                    }).ToList();
                // The user could have configured an "Other", don't duplicate it
                if (configured.All(g => g.Name != "Other"))
                {
                    configured.Add(new ApplicationGroup { Name = "Other" });
                }
                CatchAll = configured.First(g => g.Name == "Other");
                return configured;
            }
            // One big bucket if nothing is configured
            CatchAll = new ApplicationGroup { Name = "All" };
            if (applications?.Any() ?? false)
            {
                CatchAll.Applications = applications.OrderBy(a => a).Select(a => new Application { Name = a }).ToList();
            }
            return new List<ApplicationGroup>
            {
                CatchAll
            };
        }

        private Cache<List<Application>> _applications;
        public Cache<List<Application>> Applications =>
            _applications ?? (_applications = new Cache<List<Application>>(
                this,
                "Exceptions Fetch: " + Name + ":" + nameof(Applications),
                Settings.PollIntervalSeconds.Seconds(),
                async () =>
                {
                    List<Application> result;
                    if (!Settings.IsArabam)
                    {
                        result = await QueryListAsync<Application>($"Applications Fetch: {Name}", @"
Select ApplicationName as Name, 
       Sum(DuplicateCount) as ExceptionCount,
	   Sum(Case When CreationDate > DateAdd(Second, -@RecentSeconds, GETUTCDATE()) Then DuplicateCount Else 0 End) as RecentExceptionCount,
	   MAX(CreationDate) as MostRecent
  From Exceptions
 Where DeletionDate Is Null
 Group By ApplicationName", new { Current.Settings.Exceptions.RecentSeconds }).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await GetArabamApplications();
                    }

                    result.ForEach(a =>
                    {
                        a.StoreName = Name;
                        a.Store = this;
                    });
                    return result;
                },
                afterPoll: cache => UpdateApplicationGroups()));

        private async Task<List<Application>> GetArabamApplications()
        {
            return await QueryListAsync<Application>($"Applications Fetch: {TableName}", @"
  Select u.ApplicationName as Name, 
       COUNT(u.Id) as ExceptionCount,
	   Sum(Case When u.Date > DateAdd(Second, -10, GETDATE()) Then 1 Else 0 End) as RecentExceptionCount,
	   MAX(u.Date) as MostRecent
 FROM (SELECT * FROM " + TableName + @" (NOLOCK) UNION ALL SELECT * FROM " + ServiceTableName + @" (NOLOCK)
  WHERE Level='ERROR' 
  AND Exception not like 'System.Web.HttpException (0x80004005)%' 
  AND Exception not like '%System.IO.IOException: Error reading MIME multipart body%') AS u
  GROUP By u.ApplicationName",
                new { Current.Settings.Exceptions.RecentSeconds }).ConfigureAwait(false);
        }

        internal List<ApplicationGroup> UpdateApplicationGroups()
        {
            using (MiniProfiler.Current.Step(nameof(UpdateApplicationGroups)))
            {
                var result = ApplicationGroups;
                var apps = Applications.Data;
                // Loop through all configured groups and hook up applications returned from the queries
                foreach (var g in result)
                {
                    for (var i = 0; i < g.Applications.Count; i++)
                    {
                        var a = g.Applications[i];
                        foreach (var app in apps)
                        {
                            if (app.Name == a.Name)
                            {
                                g.Applications[i] = app;
                                break;
                            }
                        }
                    }
                }
                // Clear all dynamic apps from the CatchAll group unless we found them again (minimal delta)
                // Doing this atomically to minimize enumeration conflicts
                CatchAll.Applications.RemoveAll(a => !apps.Any(ap => ap.Name == a.Name) && !KnownApplications.Contains(a.Name));

                // Check for the any dyanmic/unconfigured apps that we need to add to the all group
                foreach (var app in apps)
                {
                    if (!KnownApplications.Contains(app.Name) && !CatchAll.Applications.Any(a => a.Name == app.Name))
                    {
                        // This dynamic app needs a home!
                        CatchAll.Applications.Add(app);
                    }
                }
                CatchAll.Applications.Sort((a, b) => a.Name.CompareTo(b.Name));

                // Cleanout those with no errors right now
                var foundNames = apps.Select(a => a.Name).ToHashSet();
                foreach (var a in result.SelectMany(g => g.Applications))
                {
                    if (!foundNames.Contains(a.Name))
                    {
                        a.ClearCounts();
                    }
                }

                ApplicationGroups = result;
                return result;
            }
        }

        public class SearchParams
        {
            public string Group { get; set; }
            public string Log { get; set; }
            public int Count { get; set; } = 250;
            public bool IncludeDeleted { get; set; }
            public string SearchQuery { get; set; }
            public string Message { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public Guid? StartAt { get; set; }
            public ExceptionSorts Sort { get; set; }

            public override int GetHashCode()
            {
                var hashCode = -1510201480;
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(Group);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(Log);
                hashCode = (hashCode * -1521134295) + Count.GetHashCode();
                hashCode = (hashCode * -1521134295) + IncludeDeleted.GetHashCode();
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(SearchQuery);
                hashCode = (hashCode * -1521134295) + EqualityComparer<string>.Default.GetHashCode(Message);
                hashCode = (hashCode * -1521134295) + EqualityComparer<DateTime?>.Default.GetHashCode(StartDate);
                hashCode = (hashCode * -1521134295) + EqualityComparer<DateTime?>.Default.GetHashCode(EndDate);
                hashCode = (hashCode * -1521134295) + EqualityComparer<Guid?>.Default.GetHashCode(StartAt);
                return (hashCode * -1521134295) + Sort.GetHashCode();
            }
        }

        // TODO: Move this into a SQL-specific provider maybe, something swappable
        public Task<List<Error>> GetErrorsAsync(SearchParams search)
        {
            return Settings.IsArabam
                ? GetArabamErrorsAsync(search)
                : GetExceptionalLogsAsync(search);
        }

        private Task<List<Error>> GetExceptionalLogsAsync(SearchParams search)
        {
            var sb = StringBuilderCache.Get();
            bool firstWhere = true;

            void AddClause(string clause)
            {
                sb.Append(firstWhere ? " Where " : "   And ").AppendLine(clause);
                firstWhere = false;
            }

            sb.Append(@"With list As (
Select e.Id,
	   e.GUID,
	   e.ApplicationName,
	   e.MachineName,
	   e.CreationDate,
	   e.Type,
	   e.IsProtected,
	   e.Host,
	   e.Url,
	   e.HTTPMethod,
	   e.IPAddress,
	   e.Source,
	   e.Message,
	   e.StatusCode,
	   e.ErrorHash,
	   e.DuplicateCount,
	   e.DeletionDate,
	   ROW_NUMBER() Over(").Append(GetSortString(search.Sort)).Append(@") rowNum
  From ").Append(TableName).AppendLine(" e");

            var logs = GetAppNames(search);
            if (search.Log.HasValue() || search.Group.HasValue())
            {
                AddClause("ApplicationName In @logs");
            }

            if (search.Message.HasValue())
            {
                AddClause("Message = @Message");
            }

            if (!search.IncludeDeleted)
            {
                AddClause("DeletionDate Is Null");
            }

            if (search.StartDate.HasValue)
            {
                AddClause("CreationDate >= @StartDate");
            }

            if (search.EndDate.HasValue)
            {
                AddClause("CreationDate <= @EndDate");
            }

            if (search.SearchQuery.HasValue())
            {
                AddClause("(Message Like @query Or Url Like @query)");
            }

            sb.Append(@")
  Select Top {=Count} *
    From list");
            if (search.StartAt.HasValue)
            {
                sb.Append(@"
   Where rowNum > (Select Top 1 rowNum From list Where GUID = @StartAt)");
            }

            sb.Append(@"
Order By rowNum");

            var sql = sb.ToStringRecycle();

            return QueryListAsync<Error>($"{nameof(GetErrorsAsync)}() for {Name}", sql, new
            {
                logs,
                search.Message,
                search.StartDate,
                search.EndDate,
                query = "%" + search.SearchQuery + "%",
                search.StartAt,
                search.Count
            });
        }

        public async Task<List<Error>> GetArabamErrorsAsync(SearchParams search)
        {
            var sb = StringBuilderCache.Get();
            bool firstWhere = true;

            void AddClause(string clause)
            {
                sb.Append(firstWhere ? " Where " : "   And ").AppendLine(clause);
                firstWhere = false;
            }

            sb.Append(@"With list As (
SELECT [Id]
      ,[Date]
      ,[Level]
      ,[Logger]
      ,[Message]
      ,[Exception]
      ,[MachineName]
      ,[Url]
      ,[IpAddress]
      ,[AuthToken]
      ,[ApplicationName]
      ,[ApiKey]
      ,[AppVersion]
      ,[UserAgent]
	  ,ROW_NUMBER() Over(")
                .Append(GetArabamSortString(search.Sort))
                .Append(@") rowNum FROM (SELECT * FROM " + TableName + @" (nolock) UNION ALL SELECT * FROM " +
                        ServiceTableName + " (NOLOCK) ) AS u ");

            AddClause("Level='ERROR'");
            AddClause("Exception not like 'System.Web.HttpException (0x80004005)%'");
            AddClause("Exception not like '%System.IO.IOException: Error reading MIME multipart body%'");
            var logs = GetAppNames(search);
            if (search.Log.HasValue() || search.Group.HasValue())
                AddClause("ApplicationName In @logs");
            if (search.Message.HasValue())
                AddClause("Message = @Message");
            if (search.StartDate.HasValue)
                AddClause("Date >= @StartDate");
            if (search.EndDate.HasValue)
                AddClause("Date <= @EndDate");
            if (search.SearchQuery.HasValue())
                AddClause("(Message Like @query Or Url Like @query)");

            sb.Append(@")
  Select Top {=Count} *
    From list");
            if (search.StartAt.HasValue)
            {
                sb.Append(@"
   Where rowNum > (Select Top 1 rowNum From list Where GUID = @StartAt)");
            }

            sb.Append(@" Order By rowNum");

            var sql = sb.ToStringRecycle();

            var arabamLogs = await QueryListAsync<ArabamLog>($"{nameof(GetErrorsAsync)}() for {Name}", sql, new
            {
                logs,
                search.Message,
                search.StartDate,
                search.EndDate,
                query = "%" + search.SearchQuery + "%",
                search.StartAt,
                search.Count
            });
            return arabamLogs.Select(ConvertArabamToExceptionalLog).ToList();
        }

        private Error ConvertArabamToExceptionalLog(ArabamLog s)
        {
            return new ArabamError
            {
                Exception = new Exception(s.Exception),
                Message = s.Message,
                MachineName = s.MachineName,
                ApplicationName = s.ApplicationName,
                CreationDate = s.Date,
                Id = s.Id,
                Type = s.Level,
                Detail = s.Exception,
                IPAddress = s.IpAddress,
                UrlPath = s.Url,
                Host = FindHost(s.Url),
                DuplicateCount = 1,
                AppName = FindAppName(s.ApiKey),
                AppVersion = s.AppVersion,
                AuthToken = s.AuthToken,
                UserAgent = s.UserAgent
            };
        }

        private string FindAppName(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
                return null;
            if (apiKey == Settings.AndroidApiKey)
                return "Android";
            if (apiKey == Settings.IosApiKey)
                return "IOS";
            return apiKey;
        }

        private static string FindHost(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            var host = url.Replace("https://", "").Replace("http://", "");
            var slashIndex = host.IndexOf("/", StringComparison.InvariantCulture);
            if (slashIndex <= 0)
                return host;
            return host.Substring(0, slashIndex);
        }

        public IEnumerable<string> GetAppNames(SearchParams search) => GetAppNames(search.Group, search.Log);
        public IEnumerable<string> GetAppNames(string group, string app)
        {
            if (app.HasValue())
            {
                yield return app;
                yield break;
            }
            if (group.HasValue())
            {
                var apps = ApplicationGroups?.FirstOrDefault(g => g.Name == group)?.Applications;
                if (apps != null)
                {
                    foreach (var a in apps)
                    {
                        yield return a.Name;
                    }
                }
            }
        }
        private string GetArabamSortString(ExceptionSorts sort)
        {
            switch (sort)
            {
                case ExceptionSorts.AppAsc:
                    return " Order By [ApplicationName], Date Desc";
                case ExceptionSorts.AppDesc:
                    return " Order By [ApplicationName] Desc, Date Desc";
                case ExceptionSorts.TypeAsc:
                    return " Order By Right(e.Level, charindex('.', reverse(e.Level) + '.') - 1), Date Desc";
                case ExceptionSorts.TypeDesc:
                    return " Order By Right(e.Level, charindex('.', reverse(e.Level) + '.') - 1) Desc, Date Desc";
                case ExceptionSorts.MessageAsc:
                    return " Order By Message, Date Desc";
                case ExceptionSorts.MessageDesc:
                    return " Order By Message Desc, Date Desc";
                case ExceptionSorts.UrlAsc:
                    return " Order By Url, Date Desc";
                case ExceptionSorts.UrlDesc:
                    return " Order By Url Desc, Date Desc";
                case ExceptionSorts.IPAddressAsc:
                    return " Order By IPAddress, Date Desc";
                case ExceptionSorts.IPAddressDesc:
                    return " Order By IPAddress Desc, Date Desc";
                case ExceptionSorts.HostAsc:
                    return " Order By Url, Date Desc";
                case ExceptionSorts.HostDesc:
                    return " Order By Url Desc, Date Desc";
                case ExceptionSorts.MachineNameAsc:
                    return " Order By MachineName, Date Desc";
                case ExceptionSorts.MachineNameDesc:
                    return " Order By MachineName Desc, Date Desc";
                default:
                    return " Order By Date Desc";
            }
        }

        private string GetSortString(ExceptionSorts sort)
        {
            switch (sort)
            {
                case ExceptionSorts.AppAsc:
                    return " Order By ApplicationName, CreationDate Desc";
                case ExceptionSorts.AppDesc:
                    return " Order By ApplicationName Desc, CreationDate Desc";
                case ExceptionSorts.TypeAsc:
                    return " Order By Right(e.Type, charindex('.', reverse(e.Type) + '.') - 1), CreationDate Desc";
                case ExceptionSorts.TypeDesc:
                    return " Order By Right(e.Type, charindex('.', reverse(e.Type) + '.') - 1) Desc, CreationDate Desc";
                case ExceptionSorts.MessageAsc:
                    return " Order By Message, CreationDate Desc";
                case ExceptionSorts.MessageDesc:
                    return " Order By Message Desc, CreationDate Desc";
                case ExceptionSorts.UrlAsc:
                    return " Order By Url, CreationDate Desc";
                case ExceptionSorts.UrlDesc:
                    return " Order By Url Desc, CreationDate Desc";
                case ExceptionSorts.IPAddressAsc:
                    return " Order By IPAddress, CreationDate Desc";
                case ExceptionSorts.IPAddressDesc:
                    return " Order By IPAddress Desc, CreationDate Desc";
                case ExceptionSorts.HostAsc:
                    return " Order By Host, CreationDate Desc";
                case ExceptionSorts.HostDesc:
                    return " Order By Host Desc, CreationDate Desc";
                case ExceptionSorts.MachineNameAsc:
                    return " Order By MachineName, CreationDate Desc";
                case ExceptionSorts.MachineNameDesc:
                    return " Order By MachineName Desc, CreationDate Desc";
                case ExceptionSorts.CountAsc:
                    return " Order By IsNull(DuplicateCount, 1), CreationDate Desc";
                case ExceptionSorts.CountDesc:
                    return " Order By IsNull(DuplicateCount, 1) Desc, CreationDate Desc";
                case ExceptionSorts.TimeAsc:
                    return " Order By CreationDate";
                //case ExceptionSorts.TimeDesc:
                default:
                    return " Order By CreationDate Desc";
            }
        }

        public Task<int> DeleteAllErrorsAsync(List<string> apps)
        {
            if (Settings.IsArabam)
                return null;

            return ExecTaskAsync($"{nameof(DeleteAllErrorsAsync)}() for {Name}", @"
Update Exceptions 
   Set DeletionDate = GETUTCDATE() 
 Where DeletionDate Is Null 
   And IsProtected = 0 
   And ApplicationName In @apps", new { apps });
        }

        public Task<int> DeleteSimilarErrorsAsync(Error error)
        {
            if (Settings.IsArabam)
                return null;

            return ExecTaskAsync($"{nameof(DeleteSimilarErrorsAsync)}('{error.GUID}') (app: {error.ApplicationName}) for {Name}", @"
Update Exceptions 
   Set DeletionDate = GETUTCDATE() 
 Where ApplicationName = @ApplicationName
   And Message = @Message
   And DeletionDate Is Null
   And IsProtected = 0", new { error.ApplicationName, error.Message });
        }

        public Task<int> DeleteErrorsAsync(List<Guid> ids)
        {
            if (Settings.IsArabam)
                return null;

            return ExecTaskAsync($"{nameof(DeleteErrorsAsync)}({ids.Count} Guids) for {Name}", @"
Update Exceptions 
   Set DeletionDate = GETUTCDATE() 
 Where DeletionDate Is Null 
   And IsProtected = 0 
   And GUID In @ids", new { ids });
        }

        public async Task<Error> GetErrorAsync(string app, string id)
        {
            return Settings.IsArabam
                ? await GetArabamErrorAsync(id, app)
                : await GetExceptionalErrorAsync(id);
        }

        private async Task<Error> GetArabamErrorAsync(string id, string appName)
        {
            try
            {
                ArabamLog sqlError;
                using (MiniProfiler.Current.Step(nameof(GetErrorAsync) + "() (guid: " + id + ") for " + Name))
                {
                    sqlError = await GetArabamErrorById(id, appName);
                }

                if (sqlError == null)
                    return null;

                // everything is in the JSON, but not the columns and we have to deserialize for collections anyway
                // so use that deserialized version and just get the properties that might change on the SQL side and apply them
                return ConvertArabamToExceptionalLog(sqlError);
            }
            catch (Exception e)
            {
                Current.LogException(e);
                return null;
            }
        }

        private async Task<ArabamLog> GetArabamErrorById(string id, string appName)
        {
            ArabamLog sqlError;
            using (var c = await GetConnectionAsync().ConfigureAwait(false))
            {
                var sql = @"Select Top 1 * FROM (SELECT * FROM " + TableName + @" (nolock) UNION ALL SELECT * FROM " + ServiceTableName + " (NOLOCK)) AS u Where u.Id =@id";
                if (!string.IsNullOrEmpty(appName))
                    sql += " AND [ApplicationName] = @appName";

                sqlError = await c.QueryFirstOrDefaultAsync<ArabamLog>(sql, new { id, appName }, QueryTimeout).ConfigureAwait(false);
            }

            return sqlError;
        }

        private async Task<Error> GetExceptionalErrorAsync(string guid)
        {
            try
            {
                Error sqlError;
                using (MiniProfiler.Current.Step(nameof(GetErrorAsync) + "() (guid: " + guid + ") for " + Name))
                using (var c = await GetConnectionAsync().ConfigureAwait(false))
                {
                    sqlError = await c.QueryFirstOrDefaultAsync<Error>(@"
    Select Top 1 * 
      From Exceptions 
     Where GUID = @guid", new { guid }, commandTimeout: QueryTimeout).ConfigureAwait(false);
                }

                if (sqlError == null) return null;

                // everything is in the JSON, but not the columns and we have to deserialize for collections anyway
                // so use that deserialized version and just get the properties that might change on the SQL side and apply them
                var result = Error.FromJson(sqlError.FullJson);
                result.DuplicateCount = sqlError.DuplicateCount;
                result.DeletionDate = sqlError.DeletionDate;
                result.ApplicationName = sqlError.ApplicationName;
                result.IsProtected = sqlError.IsProtected;
                return result;
            }
            catch (Exception e)
            {
                Current.LogException(e);
                return null;
            }
        }

        public async Task<bool> ProtectErrorAsync(Guid guid)
        {
            return await ExecTaskAsync($"{nameof(ProtectErrorAsync)}() (guid: {guid}) for {Name}", @"
Update Exceptions 
   Set IsProtected = 1, DeletionDate = Null
 Where GUID = @guid", new { guid }).ConfigureAwait(false) > 0;
        }

        public async Task<bool> DeleteErrorAsync(Guid guid)
        {
            return await ExecTaskAsync($"{nameof(DeleteErrorAsync)}() (guid: {guid}) for {Name}", @"
Update Exceptions 
   Set DeletionDate = GETUTCDATE() 
 Where GUID = @guid 
   And DeletionDate Is Null", new { guid }).ConfigureAwait(false) > 0;
        }

        public async Task<List<T>> QueryListAsync<T>(string step, string sql, dynamic paramsObj)
        {
            try
            {
                using (MiniProfiler.Current.Step(step))
                using (var c = await GetConnectionAsync().ConfigureAwait(false))
                {
                    return await c.QueryAsync<T>(sql, paramsObj as object, commandTimeout: QueryTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Current.LogException(e);
                return new List<T>();
            }
        }

        public async Task<int> ExecTaskAsync(string step, string sql, dynamic paramsObj)
        {
            using (MiniProfiler.Current.Step(step))
            using (var c = await GetConnectionAsync().ConfigureAwait(false))
            {
                // Perform the action
                var result = await c.ExecuteAsync(sql, paramsObj as object, commandTimeout: QueryTimeout).ConfigureAwait(false);
                // Refresh our caches
                await Applications.PollAsync(!Applications.IsPolling).ConfigureAwait(false);
                return result;
            }
        }

        private Task<DbConnection> GetConnectionAsync() =>
            Connection.GetOpenAsync(Settings.ConnectionString, QueryTimeout);
    }
}
