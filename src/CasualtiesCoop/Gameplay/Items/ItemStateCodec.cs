using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace CasualtiesCoop.Gameplay.Items;

internal static class ItemStateCodec
{
    private const byte Version = 3;
    private const byte FlagFavourited = 1;
    private const byte FlagLabel = 2;
    private const byte FlagLight = 4;
    private const byte FlagCustom = 8;
    private const float WetDuration = 120f;
    private const int MaxDepth = 8;
    private const int MaxStringBytes = 4096;
    private const int MaxCollectionItems = 4096;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Type[] ComponentTypes = BuildComponentCatalog();
    private static readonly Dictionary<Type, ushort> ComponentIndexes = ComponentTypes
        .Select((type, index) => (type, index: (ushort)index)).ToDictionary(pair => pair.type, pair => pair.index);
    private static readonly Dictionary<Type, FieldInfo[]> ComponentFields = new();
    private static readonly Dictionary<Type, FieldInfo[]> ObjectFields = new();

    internal static byte[] Capture(Item item)
    {
        using var stream = new MemoryStream(128);
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        byte flags = item.favourited ? FlagFavourited : (byte)0;
        if (!string.IsNullOrEmpty(item.label)) flags |= FlagLabel;
        LightItem? light = item.GetComponent<LightItem>();
        if (light) flags |= FlagLight;
        CustomItemBehaviour? custom = item.GetComponent<CustomItemBehaviour>();
        if (custom) flags |= FlagCustom;
        writer.Write(Version);
        writer.Write(flags);
        writer.Write((byte)Mathf.Clamp(item.colorSwatch, 0, byte.MaxValue));
        writer.Write((ushort)Mathf.Clamp(Mathf.CeilToInt(item.isWet
            ? Mathf.Max(0f, WetDuration - (Time.time - item.wetTime)) : 0f), 0, ushort.MaxValue));
        writer.Write(Mathf.Max(0f, item.decayMultiplier));
        if ((flags & FlagLabel) != 0) WriteString(writer, item.label);

        Component[] components = item.GetComponents<Component>()
            .Where(component => ComponentIndexes.ContainsKey(component.GetType())).ToArray();
        if (components.Length > byte.MaxValue ||
            components.Select(component => component.GetType()).Distinct().Count() != components.Length)
            throw new InvalidDataException();
        writer.Write((byte)components.Length);
        foreach (Component component in components)
        {
            writer.Write(ComponentIndexes[component.GetType()]);
            foreach (FieldInfo field in FieldsFor(component.GetType(), component: true))
            {
                WriteValue(writer, field.FieldType, field.GetValue(component), depth: 0);
            }
        }

        if (custom)
        {
            writer.Write(custom.state);
            WriteCustomData(writer, custom.data);
        }
        if (light)
        {
            writer.Write(light!.shouldEnable);
            writer.Write(light.inContainer);
            writer.Write(light.light && light.light.enabled);
        }
        writer.Flush();
        return stream.ToArray();
    }

    internal static bool CanApply(Item item, byte[] bytes) =>
        TryApply(item, bytes, item.condition, ItemPlacement.World, mutate: false);

    internal static bool TryApply(Item item, byte[] bytes, float condition, ItemPlacement placement) =>
        TryApply(item, bytes, condition, placement, mutate: true);

    private static bool TryApply(Item item, byte[] bytes, float condition, ItemPlacement placement, bool mutate)
    {
        if (bytes is not { Length: > 8 })
        {
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
            if (reader.ReadByte() != Version)
            {
                return false;
            }
            byte flags = reader.ReadByte();
            if ((flags & ~(FlagFavourited | FlagLabel | FlagLight | FlagCustom)) != 0) return false;
            int swatch = reader.ReadByte();
            float wetSeconds = reader.ReadUInt16();
            float decay = reader.ReadSingle();
            string? label = (flags & FlagLabel) != 0 ? ReadString(reader) : null;
            int componentCount = reader.ReadByte();
            var values = new List<(Component Component, FieldInfo Field, object? Value)>();
            var componentTypes = new HashSet<ushort>();
            bool missingComponent = false;
            for (int index = 0; index < componentCount; index++)
            {
                ushort typeIndex = reader.ReadUInt16();
                if (typeIndex >= ComponentTypes.Length || !componentTypes.Add(typeIndex))
                {
                    return false;
                }
                Type type = ComponentTypes[typeIndex];
                Component? component = item.GetComponent(type);
                if (!component) missingComponent = true;
                foreach (FieldInfo field in FieldsFor(type, component: true))
                {
                    object? value = ReadValue(reader, field.FieldType, depth: 0);
                    if (!CanAssign(field.FieldType, value)) return false;
                    if (component) values.Add((component!, field, value));
                }
            }
            int customState = 0;
            object?[]? customData = null;
            if ((flags & FlagCustom) != 0)
            {
                customState = reader.ReadInt32();
                customData = ReadCustomData(reader);
            }
            bool? lightEnabled = (flags & FlagLight) != 0 ? reader.ReadBoolean() : null;
            bool? lightInContainer = (flags & FlagLight) != 0 ? reader.ReadBoolean() : null;
            bool? lightActive = (flags & FlagLight) != 0 ? reader.ReadBoolean() : null;
            if (stream.Position != stream.Length)
            {
                return false;
            }
            if (missingComponent) return false;

            if (!mutate) return true;
            item.condition = Mathf.Clamp01(condition);
            item.favourited = (flags & FlagFavourited) != 0;
            item.label = label;
            item.colorSwatch = swatch;
            item.decayMultiplier = Mathf.Max(0f, decay);
            item.wetTime = wetSeconds > 0f ? Time.time - (WetDuration - wetSeconds) : -1000f;
            foreach (var value in values)
            {
                value.Field.SetValue(value.Component, value.Value);
            }
            CustomItemBehaviour? custom = item.GetComponent<CustomItemBehaviour>();
            if ((flags & FlagCustom) != 0)
            {
                if (!custom) return false;
                custom.state = customState;
                custom.data = customData;
            }
            LightItem? light = item.GetComponent<LightItem>();
            if ((flags & FlagLight) != 0 && !light) return false;
            if (light && lightEnabled.HasValue)
            {
                light!.shouldEnable = lightEnabled.Value;
                light.inContainer = placement == ItemPlacement.Container || lightInContainer == true;
                if (light.light) light.light.enabled = lightActive == true;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsSupported(Item item) => Resources.Load<GameObject>(item.id) is not null;

    private static Type[] BuildComponentCatalog()
    {
        Type[] types;
        try
        {
            types = typeof(Item).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        return types.Where(type => type is { IsAbstract: false } && typeof(Component).IsAssignableFrom(type) &&
            type.GetCustomAttributes(typeof(Saveable), inherit: true).Length > 0)
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
    }

    private static FieldInfo[] FieldsFor(Type type, bool component)
    {
        Dictionary<Type, FieldInfo[]> cache = component ? ComponentFields : ObjectFields;
        if (cache.TryGetValue(type, out FieldInfo[]? fields))
        {
            return fields;
        }
        fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => component
                ? field.GetCustomAttribute<JsonPropertyAttribute>(inherit: true) is not null
                : field.IsPublic && field.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is null)
            .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
        cache[type] = fields;
        return fields;
    }

    private static void WriteValue(BinaryWriter writer, Type type, object? value, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Item state nesting is too deep.");
        if (type == typeof(object)) { WriteObject(writer, value); return; }
        if (type == typeof(string)) { WriteString(writer, (string?)value); return; }
        if (type == typeof(bool)) { writer.Write((bool)(value ?? false)); return; }
        if (type.IsEnum) { WriteValue(writer, Enum.GetUnderlyingType(type), value, depth); return; }
        if (type == typeof(byte)) { writer.Write(Convert.ToByte(value ?? 0)); return; }
        if (type == typeof(sbyte)) { writer.Write(Convert.ToSByte(value ?? 0)); return; }
        if (type == typeof(short)) { writer.Write(Convert.ToInt16(value ?? 0)); return; }
        if (type == typeof(ushort)) { writer.Write(Convert.ToUInt16(value ?? 0)); return; }
        if (type == typeof(int)) { writer.Write(Convert.ToInt32(value ?? 0)); return; }
        if (type == typeof(uint)) { writer.Write(Convert.ToUInt32(value ?? 0)); return; }
        if (type == typeof(long)) { writer.Write(Convert.ToInt64(value ?? 0)); return; }
        if (type == typeof(ulong)) { writer.Write(Convert.ToUInt64(value ?? 0)); return; }
        if (type == typeof(float)) { writer.Write(Convert.ToSingle(value ?? 0f)); return; }
        if (type == typeof(double)) { writer.Write(Convert.ToDouble(value ?? 0d)); return; }
        if (type == typeof(char)) { writer.Write(Convert.ToChar(value ?? '\0')); return; }
        if (type.IsArray)
        {
            var array = (Array?)value;
            WriteCollectionCount(writer, array?.Length);
            for (int index = 0; array is not null && index < array.Length; index++)
                WriteValue(writer, type.GetElementType()!, array.GetValue(index), depth + 1);
            return;
        }
        if (typeof(IList).IsAssignableFrom(type))
        {
            var list = (IList?)value;
            WriteCollectionCount(writer, list?.Count);
            Type element = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            for (int index = 0; list is not null && index < list.Count; index++)
                WriteValue(writer, element, list[index], depth + 1);
            return;
        }
        if (!type.IsValueType)
        {
            writer.Write(value is not null);
            if (value is null) return;
            foreach (FieldInfo field in FieldsFor(type, component: false))
                WriteValue(writer, field.FieldType, field.GetValue(value), depth + 1);
            return;
        }
        throw new InvalidDataException($"Unsupported item state field type {type}.");
    }

    private static void WriteCustomData(BinaryWriter writer, object?[]? data)
    {
        WriteCollectionCount(writer, data?.Length);
        for (int index = 0; data is not null && index < data.Length; index++) WriteObject(writer, data[index]);
    }

    private static object?[]? ReadCustomData(BinaryReader reader)
    {
        int count = ReadCollectionCount(reader);
        if (count < 0) return null;
        var data = new object?[count];
        for (int index = 0; index < count; index++)
        {
            data[index] = ReadObject(reader, 0);
        }
        return data;
    }
    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null) { writer.Write(-1); return; }
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaxStringBytes) throw new InvalidDataException();
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
    private static object? ReadValue(BinaryReader reader, Type type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Item state nesting is too deep.");
        if (type == typeof(string)) return ReadString(reader);
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type.IsEnum)
        {
            object raw = ReadValue(reader, Enum.GetUnderlyingType(type), depth)!;
            object result = Enum.ToObject(type, raw);
            if (!IsValidEnum(type, result)) throw new InvalidDataException();
            return result;
        }
        if (type == typeof(byte)) return reader.ReadByte();
        if (type == typeof(sbyte)) return reader.ReadSByte();
        if (type == typeof(short)) return reader.ReadInt16();
        if (type == typeof(ushort)) return reader.ReadUInt16();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(uint)) return reader.ReadUInt32();
        if (type == typeof(long)) return reader.ReadInt64();
        if (type == typeof(ulong)) return reader.ReadUInt64();
        if (type == typeof(float)) return ReadFinite(reader.ReadSingle());
        if (type == typeof(double)) return ReadFinite(reader.ReadDouble());
        if (type == typeof(char)) return reader.ReadChar();
        if (type.IsArray)
        {
            int count = ReadCollectionCount(reader);
            if (count < 0) return null;
            Type element = type.GetElementType()!;
            Array array = Array.CreateInstance(element, count);
            for (int index = 0; index < count; index++) array.SetValue(ReadValue(reader, element, depth + 1), index);
            return array;
        }
        if (typeof(IList).IsAssignableFrom(type))
        {
            int count = ReadCollectionCount(reader);
            if (count < 0) return null;
            Type element = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            var list = (IList)Activator.CreateInstance(type)!;
            for (int index = 0; index < count; index++) list.Add(ReadValue(reader, element, depth + 1));
            return list;
        }
        if (type == typeof(object)) return ReadObject(reader, depth);
        if (!type.IsValueType)
        {
            if (!reader.ReadBoolean()) return null;
            object? instance;
            try
            {
                instance = Activator.CreateInstance(type, nonPublic: true);
            }
            catch (MissingMethodException)
            {
                instance = null;
            }
            instance ??= FormatterServices.GetUninitializedObject(type);
            foreach (FieldInfo field in FieldsFor(type, component: false))
                field.SetValue(instance, ReadValue(reader, field.FieldType, depth + 1));
            return instance;
        }
        throw new InvalidDataException($"Unsupported item state field type {type}.");
    }

    private static void WriteCollectionCount(BinaryWriter writer, int? count)
    {
        if (count is null) { writer.Write(-1); return; }
        if (count < 0 || count > MaxCollectionItems) throw new InvalidDataException();
        writer.Write(count.Value);
    }

    private static int ReadCollectionCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == -1) return -1;
        if (count is < 0 or > MaxCollectionItems) throw new InvalidDataException();
        return count;
    }

    private static void WriteObject(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.Write((byte)0); break;
            case int number: writer.Write((byte)1); writer.Write(number); break;
            case float number: writer.Write((byte)2); writer.Write(ReadFinite(number)); break;
            case bool flag: writer.Write((byte)3); writer.Write(flag); break;
            case string text: writer.Write((byte)4); WriteString(writer, text); break;
            default: throw new InvalidDataException();
        }
    }

    private static object? ReadObject(BinaryReader reader, int depth) => reader.ReadByte() switch
    {
        0 => null,
        1 => reader.ReadInt32(),
        2 => ReadFinite(reader.ReadSingle()),
        3 => reader.ReadBoolean(),
        4 => ReadString(reader),
        _ => throw new InvalidDataException()
    };

    private static float ReadFinite(float value) => float.IsFinite(value)
        ? value : throw new InvalidDataException();

    private static double ReadFinite(double value) => double.IsFinite(value)
        ? value : throw new InvalidDataException();

    private static bool IsValidEnum(Type type, object value)
    {
        if (Enum.IsDefined(type, value)) return true;
        if (type.GetCustomAttribute<FlagsAttribute>() is null) return false;
        ulong bits = Convert.ToUInt64(value);
        ulong known = 0;
        foreach (object item in Enum.GetValues(type)) known |= Convert.ToUInt64(item);
        return (bits & ~known) == 0;
    }

    private static bool CanAssign(Type type, object? value)
    {
        if (value is null) return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        return type.IsInstanceOfType(value);
    }

    private static string? ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == -1) return null;
        if (length is < 0 or > MaxStringBytes || reader.BaseStream.Length - reader.BaseStream.Position < length)
            throw new InvalidDataException();
        return Utf8.GetString(reader.ReadBytes(length));
    }
}
