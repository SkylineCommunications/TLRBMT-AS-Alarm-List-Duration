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
        private readonly GQIStringColumn _durationColumn = new GQIStringColumn("Duration");

        // Input argument definitions
        private readonly GQIStringArgument _elementNameArg = new GQIStringArgument("Element name") { IsRequired = true };
        private readonly GQIStringArgument _parameterNameArg = new GQIStringArgument("Parameter name") { IsRequired = true };
        private readonly GQIStringArgument _selectSeveritiesArg = new GQIStringArgument("Select severities") { IsRequired = true };
        private readonly GQIDateTimeArgument _dateFromArg = new GQIDateTimeArgument("Date from") { IsRequired = true };
        private readonly GQIDateTimeArgument _dateToArg = new GQIDateTimeArgument("Date to") { IsRequired = true };

        // GQIDMS connection
        private GQIDMS _dms;

        // Processed argument values
        private string _elementName;
        private string _parameterName;
        private string _selectSeverities;
        private DateTime _dateFrom;
        private DateTime _dateTo;

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

            // Collect all matching alarms and group by Root Alarm ID
            var alarmsByRootId = new Dictionary<string, List<AlarmEventMessage>>();

            foreach (var response in responses)
            {
                if (response is AlarmEventMessage alarm)
                {
                    // Filter by parameter name using wildcard matching
                    if (!MatchesWildcard(alarm.ParameterName, _parameterName))
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

                    if (!alarmsByRootId.ContainsKey(rootKey))
                    {
                        alarmsByRootId[rootKey] = new List<AlarmEventMessage>();
                    }

                    alarmsByRootId[rootKey].Add(alarm);
                }
            }

            // Process each root alarm tree to find severity transitions
            foreach (var kvp in alarmsByRootId)
            {
                // Sort alarms by TimeOfArrival
                var sortedAlarms = kvp.Value.OrderBy(a => a.TimeOfArrival).ToList();

                // Track the current alarm state
                AlarmEventMessage currentAlarm = null;

                foreach (var alarm in sortedAlarms)
                {
                    bool isNormal = string.Equals(alarm.Severity, "Normal", StringComparison.OrdinalIgnoreCase);

                    if (currentAlarm == null)
                    {
                        // First alarm in sequence - start tracking if not Normal
                        if (!isNormal)
                        {
                            currentAlarm = alarm;
                        }
                    }
                    else
                    {
                        // Check if severity changed from current
                        bool severityChanged = !string.Equals(alarm.Severity, currentAlarm.Severity, StringComparison.OrdinalIgnoreCase);

                        if (severityChanged)
                        {
                            // Create a row for the duration of the previous severity
                            var alarmOnUtc = DateTime.SpecifyKind(currentAlarm.TimeOfArrival, DateTimeKind.Utc);
                            var alarmOffUtc = DateTime.SpecifyKind(alarm.TimeOfArrival, DateTimeKind.Utc);
                            var duration = alarmOffUtc - alarmOnUtc;

                            var row = new GQIRow(
                                new GQICell[]
                                {
                                    new GQICell { Value = currentAlarm.ElementName },
                                    new GQICell { Value = currentAlarm.ParameterName },
                                    new GQICell { Value = currentAlarm.Severity },
                                    new GQICell { Value = alarmOnUtc },
                                    new GQICell { Value = alarmOffUtc },
                                    new GQICell { Value = FormatDuration(duration) },
                                });

                            rows.Add(row);

                            // If the new severity is Normal, we're done with this alarm tree
                            // Otherwise, start tracking the new severity
                            currentAlarm = isNormal ? null : alarm;
                        }

                        // If severity didn't change, this is an update to the same alarm - continue tracking
                    }
                }

                // Handle case where alarm is still active (didn't end with Normal within the time range)
                if (currentAlarm != null)
                {
                    var alarmOnUtc = DateTime.SpecifyKind(currentAlarm.TimeOfArrival, DateTimeKind.Utc);

                    var row = new GQIRow(
                        new GQICell[]
                        {
                            new GQICell { Value = currentAlarm.ElementName },
                            new GQICell { Value = currentAlarm.ParameterName },
                            new GQICell { Value = currentAlarm.Severity },
                            new GQICell { Value = alarmOnUtc },
                            new GQICell { Value = null }, // Still active, no OFF time
                            new GQICell { Value = "Active" },
                        });

                    rows.Add(row);
                }
            }

            return new GQIPage(rows.ToArray())
            {
                HasNextPage = false,
            };
        }

        private static string FormatDuration(TimeSpan duration)
        {
            // Format as HH:MM:SS (supports durations longer than 24 hours)
            int totalHours = (int)duration.TotalHours;
            return $"{totalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        /// <summary>
        /// Matches a value against a wildcard pattern.
        /// Supports * (matches any characters) and ? (matches single character).
        /// </summary>
        private static bool MatchesWildcard(string value, string pattern)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (string.IsNullOrEmpty(pattern) || pattern == "*")
            {
                return true;
            }

            // Convert wildcard pattern to regex
            // Escape special regex characters, then convert * and ? to regex equivalents
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(value, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
    }
}
