using System.Globalization;
using System.Text.RegularExpressions;
using MeetingRecorder.Application.Exceptions;

namespace MeetingRecorder.Application.Common;

public record ParsedMeetingTime(DateTime UtcDateTime, string LocalIsoString, string TimeZoneId);

public static class MeetingTimeHelper
{
    private static readonly Dictionary<string, string> IanaToWindowsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Asia/Kolkata", "India Standard Time" },
        { "Asia/Calcutta", "India Standard Time" },
        { "IST", "India Standard Time" },
        { "UTC", "UTC" },
        { "GMT", "UTC" }
    };

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }

        var clean = timeZoneId.Trim();

        // 1. Direct lookup
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(clean);
        }
        catch (Exception) { }

        // 2. Known mapping
        if (IanaToWindowsMap.TryGetValue(clean, out var winId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(winId);
            }
            catch (Exception) { }
        }

        // 3. IANA <-> Windows conversion functions
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(clean, out var convertedWinId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(convertedWinId);
            }
            catch (Exception) { }
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(clean, out var convertedIanaId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(convertedIanaId);
            }
            catch (Exception) { }
        }

        // 4. India fallback
        if (clean.Contains("kolkata", StringComparison.OrdinalIgnoreCase) ||
            clean.Contains("calcutta", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("IST", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
        }

        return TimeZoneInfo.Utc;
    }

    public static ParsedMeetingTime Parse(string rawTime, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(rawTime))
            throw new AppException("Meeting time cannot be empty.", 400, "VALIDATION_ERROR");

        var cleanTime = rawTime.Trim();
        var effectiveTzId = string.IsNullOrWhiteSpace(timeZoneId) ? "Asia/Kolkata" : timeZoneId.Trim();
        var tz = ResolveTimeZone(effectiveTzId);

        // Check if string has explicit offset like +05:30 or -04:00
        var hasExplicitOffset = Regex.IsMatch(cleanTime, @"[+-]\d{2}:?\d{2}$");
        if (hasExplicitOffset && DateTimeOffset.TryParse(cleanTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            var utc = dto.UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return new ParsedMeetingTime(utc, local.ToString("yyyy-MM-ddTHH:mm:ss"), effectiveTzId);
        }

        // If the user specified a local timezone like "Asia/Kolkata", but the string ends with 'Z' (e.g. from frontend toISOString)
        if (cleanTime.EndsWith("Z", StringComparison.OrdinalIgnoreCase) &&
            !effectiveTzId.Equals("UTC", StringComparison.OrdinalIgnoreCase) &&
            !effectiveTzId.Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            cleanTime = cleanTime[..^1];
        }

        if (DateTime.TryParse(cleanTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
            return new ParsedMeetingTime(utc, unspecified.ToString("yyyy-MM-ddTHH:mm:ss"), effectiveTzId);
        }

        throw new AppException($"Unable to parse meeting time '{rawTime}'. Please use format 'YYYY-MM-DDTHH:mm:ss'.", 400, "VALIDATION_ERROR");
    }
}
