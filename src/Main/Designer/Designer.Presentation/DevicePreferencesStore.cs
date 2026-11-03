using System;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Devices;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Persists the user's device preferences (favorites, recents, hidden ids) in the IDE's
/// user settings, once for every designer. A settings failure never breaks a preview.</summary>
public static class DevicePreferencesStore
{
    const string Favorites = "Designer.Devices.Favorites";
    const string FavoritesInitialized = "Designer.Devices.FavoritesInitialized";
    const string Recents = "Designer.Devices.Recents";
    const string Hidden = "Designer.Devices.Hidden";

    public static DevicePreferences Load()
    {
        try
        {
            return new DevicePreferences(
                DevicePreferences.Deserialize(PropertyService.Get(Favorites, "")),
                DevicePreferences.Deserialize(PropertyService.Get(Recents, "")),
                DevicePreferences.Deserialize(PropertyService.Get(Hidden, "")),
                PropertyService.Get(FavoritesInitialized, false));
        }
        catch (Exception)
        {
            return DevicePreferences.Empty;
        }
    }

    public static void Save(DevicePreferences preferences)
    {
        try
        {
            PropertyService.Set(Favorites, DevicePreferences.Serialize(preferences.Favorites));
            PropertyService.Set(FavoritesInitialized, preferences.HasFavorites);
            PropertyService.Set(Recents, DevicePreferences.Serialize(preferences.Recents));
            PropertyService.Set(Hidden, DevicePreferences.Serialize(preferences.Hidden));
        }
        catch (Exception)
        {
        }
    }
}
