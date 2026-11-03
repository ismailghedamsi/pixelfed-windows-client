using Windows.Storage;

namespace PixelfedClient.Services;

public static class ClientPreferences
{
    private const string AllowReblogKey = "feed.allowReblog";
    private const string LimitFrequentPostersKey = "feed.limitFrequentPosters24Hours";

    public static event EventHandler? FeedPreferencesChanged;

    public static bool AllowReblog =>
        ApplicationData.Current.LocalSettings.Values[AllowReblogKey] is bool enabled && enabled;

    public static bool LimitFrequentPostersToOnePostPer24Hours =>
        ApplicationData.Current.LocalSettings.Values[LimitFrequentPostersKey] is bool enabled && enabled;

    public static void SetAllowReblog(bool enabled)
    {
        if (AllowReblog == enabled)
        {
            return;
        }

        ApplicationData.Current.LocalSettings.Values[AllowReblogKey] = enabled;
        FeedPreferencesChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SetLimitFrequentPostersToOnePostPer24Hours(bool enabled)
    {
        if (LimitFrequentPostersToOnePostPer24Hours == enabled)
        {
            return;
        }

        ApplicationData.Current.LocalSettings.Values[LimitFrequentPostersKey] = enabled;
        FeedPreferencesChanged?.Invoke(null, EventArgs.Empty);
    }
}
