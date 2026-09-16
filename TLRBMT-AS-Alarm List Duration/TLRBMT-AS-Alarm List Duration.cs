using Skyline.DataMiner.Analytics.GenericInterface;
using Skyline.DataMiner.Net.Filters;
using Skyline.DataMiner.Net.Messages;
using System;
using System.Collections.Generic;
using System.Linq;

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

            // Ensure dates are in UTC
            var startTimeUtc = _dateFrom.Kind == DateTimeKind.Utc ? _dateFrom : DateTime.SpecifyKind(_dateFrom, DateTimeKind.Utc);
            var endTimeUtc = _dateTo.Kind == DateTimeKind.Utc ? _dateTo : DateTime.SpecifyKind(_dateTo, DateTimeKind.Utc);

            // Parse severities from input (comma-separated) and always include Normal for OFF detection
            var severities = _selectSeverities
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

            // Always include Normal to detect alarm OFF events for duration calculation
            if (!severities.Any(s => s.Equals("Normal", StringComparison.OrdinalIgnoreCase)))
            {
                severities.Add("Normal");
            }

            // Create set for fast lookup (case-insensitive)
            var severitySet = new HashSet<string>(severities, StringComparer.OrdinalIgnoreCase);

            // Create alarm filter - use wildcard pattern for element name at API level
            // Parameter name and severity filtering is done in code
            var filter = new AlarmFilter
            {
                FilterItems = new AlarmFilterItem[]
                {
                    new AlarmFilterItemString(
                        AlarmFilterField.ElementID,
                        AlarmFilterCompareType.WildcardEquality,
                        new string[] { _elementName }),
                },
            };

            // Get historical alarms from DataMiner using GetAlarmDetailsFromDbMessage
            // Parameters: dmaId, filter, startTime, endTime, includeAlarmDescription, includeImpactingRecords
            var alarmMessage = new GetAlarmDetailsFromDbMessage(
                -1, // All DataMiner agents
                filter,
                startTimeUtc,
                endTimeUtc,
                true,
                false);

            var responses = _dms.SendMessages(alarmMessage);

            // Precompile the parameter-name wildcard pattern once instead of per alarm
            var parameterRegex = BuildWildcardRegex(_parameterName);

            // Collect all matching alarms and group by Root Alarm ID
            var alarmsByRootId = new Dictionary<string, List<AlarmEventMessage>>();

            foreach (var response in responses)
            {
                if (response is AlarmEventMessage alarm)
                {
                    // Filter by parameter name using wildcard matching
                    if (!MatchesWildcard(alarm.ParameterName, parameterRegex))
                    {
                        continue;
                    }

                    // Filter by severity (must be in the selected severities or Normal)
                    if (!severitySet.Contains(alarm.Severity))
                    {
                        continue;
                    }

                    // Create a unique key for the root alarm tree
                    // Using TreeID which identifies the alarm tree
                    var rootKey = $"{alarm.DataMinerID}/{alarm.TreeID}";

                    if (!alarmsByRootId.TryGetValue(rootKey, out var alarmsForRoot))
                    {
                        alarmsForRoot = new List<AlarmEventMessage>();
                        alarmsByRootId[rootKey] = alarmsForRoot;
                    }

                    alarmsForRoot.Add(alarm);
                }
            }

            // Process each root alarm tree to find ON/OFF pairs
            foreach (var kvp in alarmsByRootId)
            {
                // Sort alarms by TimeOfArrival in place (avoids LINQ overhead)
                var sortedAlarms = kvp.Value;
                sortedAlarms.Sort((a, b) => a.TimeOfArrival.CompareTo(b.TimeOfArrival));

                // Find ON/OFF pairs
                AlarmEventMessage currentOnAlarm = null;

                foreach (var alarm in sortedAlarms)
                {
                    bool isNormal = string.Equals(alarm.Severity, "Normal", StringComparison.OrdinalIgnoreCase);

                    if (!isNormal && currentOnAlarm == null)
                    {
                        // This is an Alarm ON event
                        currentOnAlarm = alarm;
                    }
                    else if (isNormal && currentOnAlarm != null)
                    {
                        // This is an Alarm OFF event - create a row with duration
                        var alarmOnUtc = DateTime.SpecifyKind(currentOnAlarm.TimeOfArrival, DateTimeKind.Utc);
                        var alarmOffUtc = DateTime.SpecifyKind(alarm.TimeOfArrival, DateTimeKind.Utc);
                        var duration = alarmOffUtc - alarmOnUtc;

                        var row = new GQIRow(
                            new GQICell[]
                            {
                                new GQICell { Value = currentOnAlarm.ElementName },
                                new GQICell { Value = currentOnAlarm.ParameterName },
                                new GQICell { Value = currentOnAlarm.Severity },
                                new GQICell { Value = alarmOnUtc },
                                new GQICell { Value = alarmOffUtc },
                                new GQICell { Value = duration.TotalSeconds },
                            });

                        rows.Add(row);

                        // Reset for next potential ON alarm in the same tree
                        currentOnAlarm = null;
                    }
                    else if (!isNormal && currentOnAlarm != null)
                    {
                        // Severity changed but not to Normal (e.g., Warning -> Critical)
                        // Keep the original ON alarm, don't create a new row yet
                    }
                }

                // Handle case where alarm is still ON (no OFF found within the time range)
                if (currentOnAlarm != null)
                {
                    var alarmOnUtc = DateTime.SpecifyKind(currentOnAlarm.TimeOfArrival, DateTimeKind.Utc);

                    var row = new GQIRow(
                        new GQICell[]
                        {
                            new GQICell { Value = currentOnAlarm.ElementName },
                            new GQICell { Value = currentOnAlarm.ParameterName },
                            new GQICell { Value = currentOnAlarm.Severity },
                            new GQICell { Value = alarmOnUtc },
                            new GQICell { Value = null }, // Still active, no OFF time
                            new GQICell { Value = null }, // Active alarm, duration not yet known
                        });

                    rows.Add(row);
                }
            }

            return new GQIPage(rows.ToArray())
            {
                HasNextPage = false,
            };
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
