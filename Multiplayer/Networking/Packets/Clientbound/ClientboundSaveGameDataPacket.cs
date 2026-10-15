using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ServicePenalty;
using DV.UserManagement;
using Multiplayer.Components.Networking;
using Multiplayer.Components.SaveGame;
using Multiplayer.Networking.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundSaveGameDataPacket
{
    public string GameMode { get; set; }
    public string SerializedDifficulty { get; set; }
    public float Money { get; set; }
    public string[] AcquiredGeneralLicenses { get; set; }
    public string[] AcquiredJobLicenses { get; set; }
    public string[] UnlockedGarages { get; set; }
    public Vector3 Position { get; set; }
    public float Rotation { get; set; }

    public bool HasDebt { get; set; }
    public string SerializedInventory { get; set; }
    // public string[] Debt_existing_locos { get; set; }
    // public string[] Debt_deleted_locos { get; set; }
    // public string[] Debt_existing_jobs { get; set; }
    // public string[] Debt_staged_jobs { get; set; }
    // public string Debt_existing_jobless_cars { get; set; }
    // public string Debt_deleted_jobless_cars { get; set; }
    // public string Debt_insurance { get; set; }

    public static ClientboundSaveGameDataPacket CreatePacket(ServerPlayer player)
    {
        if (WorldStreamingInit.isLoaded)
            SaveGameManager.Instance.UpdateInternalData();

        SaveGameData data = SaveGameManager.Instance.data;

        JObject difficulty = new();
        DifficultyDataUtils.SetDifficultyToJSON(difficulty, NetworkLifecycle.Instance.Server.Difficulty);

        JObject playerData = NetworkedSaveGameManager.Instance.Server_GetPlayerData(data, player.Guid);

        return new ClientboundSaveGameDataPacket {
            GameMode = data.GetString(SaveGameKeys.Game_mode),
            SerializedDifficulty = difficulty.ToString(Formatting.None),
            Money = StartingItemsController.Instance == null || !StartingItemsController.Instance.itemsLoaded ? data.GetFloat(SaveGameKeys.Player_money).GetValueOrDefault(0) : (float)Inventory.Instance.PlayerMoney,
            AcquiredGeneralLicenses = data.GetStringArray(SaveGameKeys.Licenses_General),
            AcquiredJobLicenses = data.GetStringArray(SaveGameKeys.Licenses_Jobs),
            UnlockedGarages = data.GetStringArray(SaveGameKeys.Garages),
            Position = playerData?.GetVector3(SaveGameKeys.Player_position) ?? LevelInfo.DefaultSpawnPosition,
            Rotation = playerData?.GetFloat(SaveGameKeys.Player_rotation) ?? LevelInfo.DefaultSpawnRotation.y,
            HasDebt = data.GetFloat(SaveGameKeys.Debt_total).GetValueOrDefault(CareerManagerDebtController.Instance != null ? CareerManagerDebtController.Instance.NumberOfNonZeroPricedDebts : 0) > 0,
            SerializedInventory = SerializeInventory()
            // Debt_existing_locos = data.GetJObjectArray(SaveGameKeys.Debt_existing_locos)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_deleted_locos = data.GetJObjectArray(SaveGameKeys.Debt_deleted_locos)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_existing_jobs = data.GetJObjectArray(SaveGameKeys.Debt_existing_jobs)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_staged_jobs = data.GetJObjectArray(SaveGameKeys.Debt_staged_jobs)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_existing_jobless_cars = data.GetJObject(SaveGameKeys.Debt_existing_jobless_cars)?.ToString(),
            // Debt_deleted_jobless_cars = data.GetJObject(SaveGameKeys.Debt_deleted_jobless_cars)?.ToString(),
            // Debt_insurance = data.GetJObject(SaveGameKeys.Debt_insurance)?.ToString()
        };
    }

    public ClientboundSaveGameDataPacket Clone()
    {
        return MemberwiseClone() as ClientboundSaveGameDataPacket;
    }

    private static string SerializeInventory()
    {
        try
        {
            object inventory = Inventory.Instance;
            if (inventory == null)
                return null;

            IEnumerable items = GetInventoryItemsEnumerable(inventory);
            if (items == null)
                return null;

            JArray array = new JArray();
            foreach (object item in items)
            {
                if (item == null)
                    continue;

                string itemId = GetInventoryItemId(item);
                int amount = GetInventoryItemAmount(item);
                if (string.IsNullOrEmpty(itemId) || amount <= 0)
                    continue;

                array.Add(new JObject
                {
                    ["itemId"] = itemId,
                    ["amount"] = amount
                });
            }

            return array.Count > 0 ? array.ToString(Formatting.None) : null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Multiplayer] Failed to serialize inventory: {ex}");
            return null;
        }
    }

    public static void ApplyInventorySnapshot(string serializedInventory)
    {
        if (string.IsNullOrEmpty(serializedInventory))
            return;

        try
        {
            JArray array = JArray.Parse(serializedInventory);
            object inventory = Inventory.Instance;
            if (inventory == null)
                return;

            TryClearInventory(inventory);

            foreach (JObject item in array.OfType<JObject>())
            {
                if (item == null)
                    continue;

                string itemId = item.Value<string>("itemId");
                int amount = item.Value<int?>("amount") ?? 0;
                if (string.IsNullOrEmpty(itemId) || amount <= 0)
                    continue;

                if (!TryAddInventoryItem(inventory, itemId, amount))
                    Debug.LogWarning($"[Multiplayer] Failed to restore inventory item '{itemId}' x{amount}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Multiplayer] Failed to apply inventory snapshot: {ex}");
        }
    }

    private static IEnumerable GetInventoryItemsEnumerable(object inventory)
    {
        Type inventoryType = inventory.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string[] candidateNames = { "Items", "InventoryItems", "ItemStacks", "PlayerItems", "PlayerInventory", "Inventory" };

        foreach (string name in candidateNames)
        {
            PropertyInfo property = inventoryType.GetProperty(name, flags);
            if (property != null)
            {
                object value = property.GetValue(inventory);
                if (value is IEnumerable enumerable)
                    return enumerable;
            }

            FieldInfo field = inventoryType.GetField(name, flags);
            if (field != null)
            {
                object value = field.GetValue(inventory);
                if (value is IEnumerable enumerable)
                    return enumerable;
            }

            MethodInfo method = inventoryType.GetMethod($"Get{name}", flags, null, Type.EmptyTypes, null);
            if (method != null)
            {
                object value = method.Invoke(inventory, null);
                if (value is IEnumerable enumerable)
                    return enumerable;
            }
        }

        MethodInfo itemsMethod = inventoryType.GetMethod("GetItems", flags, null, Type.EmptyTypes, null) ?? inventoryType.GetMethod("EnumerateItems", flags, null, Type.EmptyTypes, null);
        if (itemsMethod != null)
        {
            object value = itemsMethod.Invoke(inventory, null);
            if (value is IEnumerable enumerable)
                return enumerable;
        }

        if (inventory is IEnumerable inventoryEnumerable)
            return inventoryEnumerable;

        return null;
    }

    private static string GetInventoryItemId(object item)
    {
        if (item == null)
            return null;

        if (item is string stringItem)
            return stringItem;

        object keyValue = GetPropertyOrFieldValue(item, "Key") ?? GetPropertyOrFieldValue(item, "key");
        object valueValue = GetPropertyOrFieldValue(item, "Value") ?? GetPropertyOrFieldValue(item, "value");
        if (keyValue != null && valueValue != null)
            item = keyValue;

        object candidate = GetPropertyOrFieldValue(item, "itemId") ?? GetPropertyOrFieldValue(item, "ItemId") ?? GetPropertyOrFieldValue(item, "id") ?? GetPropertyOrFieldValue(item, "Id") ?? GetPropertyOrFieldValue(item, "name") ?? GetPropertyOrFieldValue(item, "Name");
        if (candidate is string candidateString)
            return candidateString;

        return candidate?.ToString() ?? item.ToString();
    }

    private static int GetInventoryItemAmount(object item)
    {
        if (item == null)
            return 0;

        if (item is KeyValuePair<object, object> kvp)
        {
            return ConvertToInt(kvp.Value);
        }

        object value = GetPropertyOrFieldValue(item, "amount") ?? GetPropertyOrFieldValue(item, "Amount") ?? GetPropertyOrFieldValue(item, "count") ?? GetPropertyOrFieldValue(item, "Count") ?? GetPropertyOrFieldValue(item, "quantity") ?? GetPropertyOrFieldValue(item, "Quantity") ?? GetPropertyOrFieldValue(item, "stackSize") ?? GetPropertyOrFieldValue(item, "StackSize");
        if (value == null)
            return 0;

        return ConvertToInt(value);
    }

    private static object GetPropertyOrFieldValue(object obj, string name)
    {
        if (obj == null)
            return null;

        Type type = obj.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo property = type.GetProperty(name, flags);
        if (property != null)
            return property.GetValue(obj);

        FieldInfo field = type.GetField(name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private static int ConvertToInt(object value)
    {
        if (value == null)
            return 0;

        try
        {
            if (value is int intValue)
                return intValue;
            if (value is long longValue)
                return (int)longValue;
            if (value is float floatValue)
                return (int)floatValue;
            if (value is double doubleValue)
                return (int)doubleValue;
            if (value is string stringValue && int.TryParse(stringValue, out int parsed))
                return parsed;
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryClearInventory(object inventory)
    {
        if (inventory == null)
            return false;

        Type inventoryType = inventory.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string[] candidates = { "Clear", "ClearInventory", "RemoveAllItems", "Empty", "Reset" };

        foreach (string candidate in candidates)
        {
            MethodInfo method = inventoryType.GetMethod(candidate, flags, null, Type.EmptyTypes, null);
            if (method != null)
            {
                try
                {
                    method.Invoke(inventory, null);
                    return true;
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static bool TryAddInventoryItem(object inventory, string itemId, int amount)
    {
        if (inventory == null || string.IsNullOrEmpty(itemId) || amount <= 0)
            return false;

        Type inventoryType = inventory.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string[] candidateNames = { "AddItem", "GiveItem", "Add", "AddStack", "AddInventoryItem" };

        foreach (string candidate in candidateNames)
        {
            MethodInfo[] methods = inventoryType.GetMethods(flags).Where(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (MethodInfo method in methods)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[1].ParameterType == typeof(int))
                {
                    object arg0 = parameters[0].ParameterType == typeof(string)
                        ? itemId
                        : ResolveItemType(itemId, parameters[0].ParameterType);
                    if (arg0 == null)
                        continue;

                    try
                    {
                        method.Invoke(inventory, new[] { arg0, amount });
                        return true;
                    }
                    catch
                    {
                    }
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string) && amount == 1)
                {
                    try
                    {
                        method.Invoke(inventory, new object[] { itemId });
                        return true;
                    }
                    catch
                    {
                    }
                }
            }
        }

        return false;
    }

    private static object ResolveItemType(string itemId, Type targetType)
    {
        if (targetType == typeof(string))
            return itemId;

        MethodInfo staticFinder = targetType.GetMethod("GetById", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? targetType.GetMethod("GetByID", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? targetType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? targetType.GetMethod("Find", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);

        if (staticFinder != null)
        {
            try
            {
                object result = staticFinder.Invoke(null, new object[] { itemId });
                if (result != null && targetType.IsInstanceOfType(result))
                    return result;
            }
            catch
            {
            }
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type type in assembly.GetTypes())
            {
                MethodInfo finder = type.GetMethod("GetById", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
                    ?? type.GetMethod("GetByID", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
                    ?? type.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
                    ?? type.GetMethod("Find", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (finder == null || !targetType.IsAssignableFrom(finder.ReturnType))
                    continue;

                try
                {
                    object result = finder.Invoke(null, new object[] { itemId });
                    if (result != null)
                        return result;
                }
                catch
                {
                }
            }
        }

        ConstructorInfo ctor = targetType.GetConstructor(new[] { typeof(string) });
        if (ctor != null)
        {
            try
            {
                return ctor.Invoke(new object[] { itemId });
            }
            catch
            {
            }
        }

        return null;
    }
}
