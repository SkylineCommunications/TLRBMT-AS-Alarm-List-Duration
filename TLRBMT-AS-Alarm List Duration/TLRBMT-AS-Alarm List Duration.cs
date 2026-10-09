using Skyline.DataMiner.Analytics.GenericInterface;
using Skyline.DataMiner.Net.Filters;
using Skyline.DataMiner.Net.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace TLRBMTASAlarmListDuration
{
    /// <summary>
    /// Represents a data source.
    /// See: https://aka.dataminer.services/gqi-external-data-source for a complete example.
    /// </summary>
    [GQIMetaData(Name = "TLRBMT-AS-Alarm List Duration")]
    public sealed class TLRBMTASAlarmListDuration : IGQIDataSource
        , IGQIOnInit
        , IGQIInputArguments
    {
        // Output column definitions
        private readonly GQIStringColumn _elementColumn = new GQIStringColumn("Element");
        private readonly GQIStringColumn _parameterColumn = new GQIStringColumn("Parameter");
        private readonly GQIStringColumn _severityColumn = new GQIStringColumn("Severity");
        private readonly GQIDateTimeColumn _alarmOnColumn = new GQIDateTimeColumn("Alarm On");
        private readonly GQIDateTimeColumn _alarmOffColumn = new GQIDateTimeColumn("Alarm Off");
        private readonly GQIDoubleColumn _durationColumn = new GQIDoubleColumn("Duration (s)");

        // Input argument definitions
        private readonly GQIStringArgument _elementNameArg = new GQIStringArgument("Element name") { IsRequired = true };
        private readonly GQIStringArgument _parameterNameArg = new GQIStringArgument("Parameter name") { IsRequired = true };
        private readonly GQIStringArgument _selectSeveritiesArg = new GQIStringArgument("Select severities") { IsRequired = true };
        private readonly GQIDateTimeArgument _dateFromArg = new GQIDateTimeArgument("Date from") { IsRequired = true };
        private readonly GQIDateTimeArgument _dateToArg = new GQIDateTimeArgument("Date to") { IsRequired = true };
        private readonly GQIBooleanArgument _provideResultArg = new GQIBooleanArgument("Provide Result") { IsRequired = true };

        // GQIDMS connection
        private GQIDMS _dms;

        // Processed argument values
        private string _elementName;
        private string _parameterName;
        private string _selectSeverities;
        private DateTime _dateFrom;
        private DateTime _dateTo;
        private bool _provideResult;

        // Paging state: the search walks the range one window per page
        private static readonly TimeSpan WindowSize = TimeSpan.FromDays(1);
        private DateTime? _windowStart;
        private DateTime _endUtc;
        private HashSet<string> _severitySet;
        private System.Text.RegularExpressions.Regex _parameterRegex;
        private readonly Dictionary<string, AlarmEventMessage> _openAlarms = new Dictionary<string, AlarmEventMessage>();
        private readonly HashSet<string> _seenEvents = new HashSet<string>();
        private int _returnedEventCount;
        private readonly Stopwatch _searchStopwatch = new Stopwatch();
        private const int ParallelQueries = 5;

        // Stay below the ~5 minute GQI timeout so partial results are returned instead of an error
        private static readonly TimeSpan SearchTimeBudget = TimeSpan.FromMinutes(4);
        private int _windowCount;
        private TimeSpan _slowestWindow;
        private DateTime _slowestWindowStart;

        // Null means no element filter; otherwise "dmaID/elementID" keys for the DB query
        private string[] _elementIds;
        private DateTime _nextQueryStart;
        private readonly Queue<Tuple<DateTime, Task<WindowResult>>> _pending = new Queue<Tuple<DateTime, Task<WindowResult>>>();

        /// <inheritdoc />
        public OnInitOutputArgs OnInit(OnInitInputArgs args)
        {
            _dms = args.DMS;
            return default;
        }

        /// <inheritdoc />
        public GQIArgument[] GetInputArguments()
        {
            return new GQIArgument[]
            {
                _elementNameArg,
                _parameterNameArg,
                _selectSeveritiesArg,
                _dateFromArg,
                _dateToArg,
                _provideResultArg,
            };
        }

        /// <inheritdoc />
        public OnArgumentsProcessedOutputArgs OnArgumentsProcessed(OnArgumentsProcessedInputArgs args)
        {
            _elementName = args.GetArgumentValue(_elementNameArg);
            _parameterName = args.GetArgumentValue(_parameterNameArg);
            _selectSeverities = args.GetArgumentValue(_selectSeveritiesArg);
            _dateFrom = args.GetArgumentValue(_dateFromArg);
            _dateTo = args.GetArgumentValue(_dateToArg);
            _provideResult = args.GetArgumentValue(_provideResultArg);
            _windowStart = null;

            return default;
        }

        /// <inheritdoc />
        public GQIColumn[] GetColumns()
        {
            return new GQIColumn[]
            {
                _elementColumn,
                _parameterColumn,
                _severityColumn,
                _alarmOnColumn,
                _alarmOffColumn,
                _durationColumn,
            };
        }

        /// <inheritdoc />
        public GQIPage GetNextPage(GetNextPageInputArgs args)
        {
            var rows = new List<GQIRow>();

            // Skip the full search and return a quick prompt row when result generation is disabled
            if (!_provideResult)
            {
                var promptRow = new GQIRow(
                    new GQICell[]
                    {
                        new GQICell { Value = "Enable 'Provide Result' to run the search." },
                        new GQICell { Value = string.Empty },
                        new GQICell { Value = string.Empty },
                        new GQICell { Value = null },
                        new GQICell { Value = null },
                        new GQICell { Value = null },
                    });

                rows.Add(promptRow);

                return new GQIPage(rows.ToArray())
                {
                    HasNextPage = false,
                };
            }

            if (_windowStart == null)
            {
                InitializeSearch();

                if (_elementIds != null && _elementIds.Length == 0)
                {
                    rows.Add(CreateInfoRow($"[Info] No elements match '{_elementName}'."));
                    return new GQIPage(rows.ToArray()) { HasNextPage = false };
                }

                // Return at once so the user sees the search is running
                var days = (int)Math.Ceiling((_endUtc - _windowStart.Value).TotalDays);
                var elementInfo = _elementIds == null ? "all elements" : $"{_elementIds.Length} matching elements";
                rows.Add(CreateInfoRow($"[Info] Searching {_windowStart.Value:yyyy-MM-dd HH:mm} to {_endUtc:yyyy-MM-dd HH:mm} UTC for {elementInfo} in {Math.Max(days, 0)} daily steps. The alarm count is shown in the last row."));

                return new GQIPage(rows.ToArray())
                {
                    HasNextPage = _windowStart.Value < _endUtc,
                };
            }

            StartQueries();

            if (_pending.Count == 0)
            {
                return FinishSearch(rows, _nextQueryStart < _endUtc ? StopMessage(_nextQueryStart) : null);
            }

            // Never wait past the time budget, even if a single day is still running
            var oldest = _pending.Peek();
            var remaining = SearchTimeBudget - _searchStopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !oldest.Item2.Wait(remaining))
            {
                return FinishSearch(rows, StopMessage(oldest.Item1));
            }

            // Process in time order so ON/OFF pairing spans windows correctly
            while (_pending.Count > 0 && _pending.Peek().Item2.IsCompleted)
            {
                var done = _pending.Dequeue();
                ProcessWindow(done.Item1, done.Item2.Result, rows);
            }

            StartQueries();

            if (_pending.Count == 0)
            {
                return FinishSearch(rows, _nextQueryStart < _endUtc ? StopMessage(_nextQueryStart) : null);
            }

            return new GQIPage(rows.ToArray())
            {
                HasNextPage = true,
            };
        }

        private void StartQueries()
        {
            while (_pending.Count < ParallelQueries
                && _nextQueryStart < _endUtc
                && _searchStopwatch.Elapsed < SearchTimeBudget)
            {
                var start = _nextQueryStart;
                var end = start + WindowSize;
                if (end > _endUtc)
                {
                    end = _endUtc;
                }

                _pending.Enqueue(Tuple.Create(start, Task.Run(() => QueryWindow(start, end))));
                _nextQueryStart = end;
            }
        }

        private static string StopMessage(DateTime stoppedAt)
        {
            return $"[Info] Stopped early at {stoppedAt:yyyy-MM-dd HH:mm} UTC: the time limit of {SearchTimeBudget.TotalMinutes:F0} minutes was reached. Results after this time are missing; narrow the element, parameter or date range.";
        }

        private sealed class WindowResult
        {
            public DMSMessage[] Responses { get; set; }

            public TimeSpan Elapsed { get; set; }
        }

        private WindowResult QueryWindow(DateTime windowStart, DateTime windowEnd)
        {
            var filterItems = new List<AlarmFilterItem>();
            if (_elementIds != null)
            {
                filterItems.Add(new AlarmFilterItemString(
                    AlarmFilterField.ElementID,
                    AlarmFilterCompareType.Equality,
                    _elementIds));
            }

            filterItems.Add(new AlarmFilterItemString(
                AlarmFilterField.ParameterDescription,
                AlarmFilterCompareType.WildcardEquality,
                new string[] { _parameterName }));

            var filter = new AlarmFilter
            {
                FilterItems = filterItems.ToArray(),
            };

            // Parameters: dmaId, filter, startTime, endTime, includeAlarmDescription, includeImpactingRecords
            // Descriptions are required: without them ElementName/ParameterName come back empty
            var alarmMessage = new GetAlarmDetailsFromDbMessage(-1, filter, windowStart, windowEnd, true, false);
            var timer = Stopwatch.StartNew();
            var responses = _dms.SendMessages(alarmMessage);
            timer.Stop();

            return new WindowResult { Responses = responses, Elapsed = timer.Elapsed };
        }

        private void ProcessWindow(DateTime windowStart, WindowResult result, List<GQIRow> rows)
        {
            _windowCount++;
            if (result.Elapsed > _slowestWindow)
            {
                _slowestWindow = result.Elapsed;
                _slowestWindowStart = windowStart;
            }

            var events = new List<AlarmEventMessage>();
            foreach (var response in result.Responses ?? new DMSMessage[0])
            {
                if (!(response is AlarmEventMessage alarm))
                {
                    continue;
                }

                _returnedEventCount++;

                if (!MatchesWildcard(alarm.ParameterName, _parameterRegex)
                    || !_severitySet.Contains(alarm.Severity))
                {
                    continue;
                }

                // Adjacent windows can return the same event
                var eventKey = $"{alarm.DataMinerID}/{alarm.AlarmID}/{alarm.TimeOfArrival.Ticks}/{alarm.Severity}";
                if (_seenEvents.Add(eventKey))
                {
                    events.Add(alarm);
                }
            }

            events.Sort((a, b) => a.TimeOfArrival.CompareTo(b.TimeOfArrival));

            foreach (var alarm in events)
            {
                var rootKey = $"{alarm.DataMinerID}/{alarm.TreeID}";
                var isNormal = string.Equals(alarm.Severity, "Normal", StringComparison.OrdinalIgnoreCase);

                if (!_openAlarms.TryGetValue(rootKey, out var onAlarm))
                {
                    if (!isNormal)
                    {
                        _openAlarms[rootKey] = alarm;
                    }
                }
                else if (isNormal)
                {
                    var alarmOnUtc = onAlarm.TimeOfArrival.ToUniversalTime();
                    var alarmOffUtc = alarm.TimeOfArrival.ToUniversalTime();
                    rows.Add(CreateRow(onAlarm, alarmOnUtc, alarmOffUtc, (alarmOffUtc - alarmOnUtc).TotalSeconds));
                    _openAlarms.Remove(rootKey);
                }
            }
        }

        private GQIPage FinishSearch(List<GQIRow> rows, string stopReason)
        {
            // Alarms with no OFF in the searched range are still active
            foreach (var onAlarm in _openAlarms.Values)
            {
                rows.Add(CreateRow(onAlarm, onAlarm.TimeOfArrival.ToUniversalTime(), null, null));
            }

            _openAlarms.Clear();

            var summary = $"[Info] Query returned {_returnedEventCount} alarm events in {_searchStopwatch.Elapsed.TotalSeconds:F2} seconds over {_windowCount} daily steps ({ParallelQueries} in parallel).";
            if (_windowCount > 0)
            {
                summary += $" Slowest day: {_slowestWindowStart:yyyy-MM-dd} ({_slowestWindow.TotalSeconds:F2} s).";
            }

            rows.Insert(0, CreateInfoRow(summary));
            if (stopReason != null)
            {
                rows.Insert(0, CreateInfoRow(stopReason));
            }

            return new GQIPage(rows.ToArray())
            {
                HasNextPage = false,
            };
        }

        private static GQIRow CreateInfoRow(string message)
        {
            return new GQIRow(
                new GQICell[]
                {
                    new GQICell { Value = message },
                    new GQICell { Value = string.Empty },
                    new GQICell { Value = string.Empty },
                    new GQICell { Value = null },
                    new GQICell { Value = null },
                    new GQICell { Value = null },
                });
        }

        private void InitializeSearch()
        {
            _endUtc = _dateTo.ToUniversalTime();
            _windowStart = _dateFrom.ToUniversalTime();
            _openAlarms.Clear();
            _seenEvents.Clear();
            _returnedEventCount = 0;
            _searchStopwatch.Restart();
            _windowCount = 0;
            _slowestWindow = TimeSpan.Zero;
            _nextQueryStart = _windowStart.Value;
            _pending.Clear();

            // Resolve names to IDs so the database filters by element instead of scanning all alarms
            var elementRegex = BuildWildcardRegex(_elementName);
            _elementIds = elementRegex == null
                ? null
                : _dms.SendMessages(new GetInfoMessage(InfoType.ElementInfo))
                    .OfType<ElementInfoEventMessage>()
                    .Where(e => MatchesWildcard(e.Name, elementRegex))
                    .Select(e => $"{e.DataMinerID}/{e.ElementID}")
                    .Distinct()
                    .ToArray();

            // Normal is always needed to detect alarm OFF events
            _severitySet = new HashSet<string>(
                _selectSeverities.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase)
            {
                "Normal",
            };

            _parameterRegex = BuildWildcardRegex(_parameterName);
        }

        private static GQIRow CreateRow(AlarmEventMessage alarm, DateTime onUtc, DateTime? offUtc, double? durationSeconds)
        {
            return new GQIRow(
                new GQICell[]
                {
                    new GQICell { Value = alarm.ElementName },
                    new GQICell { Value = alarm.ParameterName },
                    new GQICell { Value = alarm.Severity },
                    new GQICell { Value = onUtc },
                    new GQICell { Value = offUtc },
                    new GQICell { Value = durationSeconds },
                });
        }



        /// <summary>
        /// Builds a compiled regex for a wildcard pattern (* and ?), or null when the pattern matches everything.
        /// </summary>
        private static System.Text.RegularExpressions.Regex BuildWildcardRegex(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*")
            {
                return null;
            }

            // Escape special regex characters, then convert * and ? to regex equivalents
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return new System.Text.RegularExpressions.Regex(
                regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        }

        /// <summary>
        /// Matches a value against a precompiled wildcard regex. A null regex means "match everything".
        /// </summary>
        private static bool MatchesWildcard(string value, System.Text.RegularExpressions.Regex pattern)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return pattern == null || pattern.IsMatch(value);
        }
    }
}
