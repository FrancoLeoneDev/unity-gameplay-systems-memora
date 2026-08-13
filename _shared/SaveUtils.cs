using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class SaveUtils
{
    static JsonSerializer sharedSerializer;

    public static void Initialize(JsonSerializer serializer)
    {
        sharedSerializer = serializer;
    }

    public static T Deserialize<T>(object state)
    {
        if (state == null) return default;

        if (state is T directCast)
            return directCast;

        if (state is JObject jObj)
        {
            if (sharedSerializer == null)
                throw new InvalidOperationException(
                    "SaveUtils not initialized. SaveManager.Awake() must call SaveUtils.Initialize() before any Deserialize<T> calls.");
            return jObj.ToObject<T>(sharedSerializer);
        }

        if (state is JArray jArr)
        {
            if (sharedSerializer == null)
                throw new InvalidOperationException(
                    "SaveUtils not initialized. SaveManager.Awake() must call SaveUtils.Initialize() before any Deserialize<T> calls.");
            return jArr.ToObject<T>(sharedSerializer);
        }

        // Handle numeric type mismatches: JSON deserializes int→long, float→double
        var targetType = typeof(T);
        if (targetType == typeof(bool) && state is bool b)
            return (T)(object)b;

        try
        {
            return (T)Convert.ChangeType(state, targetType);
        }
        catch (Exception e)
        {
            throw new InvalidCastException(
                $"SaveUtils.Deserialize<{targetType.Name}>: Cannot convert {state.GetType().Name} value '{state}' to {targetType.Name}.", e);
        }
    }
}
