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
    private const byte Version = 2;
    private const byte FlagFavourited = 1;
    private const byte FlagLabel = 2;
    private const byte FlagLight = 4;
    private const byte FlagCustom = 8;
    private const float WetDuration = 120f;
    private const int MaxDepth = 4;
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
        writer.Write(Version);
        writer.Write(flags);
        writer.Write((byte)Mathf.Clamp(item.colorSwatch, 0, byte.MaxValue));
        writer.Write((ushort)Mathf.Clamp(Mathf.CeilToInt(item.isWet
            ? Mathf.Max(0f, WetDuration - (Time.time - item.wetTime)) : 0f), 0, ushort.MaxValue));
        writer.Write(Mathf.Max(0f, item.decayMultiplier));
        if ((flags & FlagLabel) != 0) WriteString(writer, item.label);

        Component[] components = item.GetComponents<Component>()
            .Where(component => ComponentIndexes.ContainsKey(component.GetType())).ToArray();
        writer.Write((byte)Math.Min(components.Length, byte.MaxValue));
        foreach (Component component in components)
        {
            writer.Write(ComponentIndexes[component.GetType()]);
            foreach (FieldInfo field in FieldsFor(component.GetType(), component: true))
            {
                WriteValue(writer, field.FieldType, field.GetValue(component), depth: 0);
            }
        }

        if (light) writer.Write(light!.shouldEnable);
        writer.Flush();
        return stream.ToArray();
    }

    internal static bool TryApply(Item item, byte[] bytes, float condition, ItemPlacement placement)
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
            int swatch = reader.ReadByte();
            float wetSeconds = reader.ReadUInt16();
            float decay = reader.ReadSingle();
            string? label = (flags & FlagLabel) != 0 ? ReadString(reader) : null;
            int componentCount = reader.ReadByte();
            var values = new List<(Component Component, FieldInfo Field, object? Value)>();
            for (int index = 0; index < componentCount; index++)
            {
                ushort typeIndex = reader.ReadUInt16();
                if (typeIndex >= ComponentTypes.Length)
                {
                    return false;
                }
                Type type = ComponentTypes[typeIndex];
                Component? component = item.GetComponent(type);
                foreach (FieldInfo field in FieldsFor(type, component: true))
                {
                    object? value = ReadValue(reader, field.FieldType, depth: 0);
                    if (component) values.Add((component!, field, value));
                }
            }
            bool? lightEnabled = (flags & FlagLight) != 0 ? reader.ReadBoolean() : null;
            if (stream.Position != stream.Length)
            {
                return false;
            }

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
            LightItem? light = item.GetComponent<LightItem>();
            if (light && lightEnabled.HasValue)
            {
                light!.shouldEnable = lightEnabled.Value;
                light.inContainer = placement == ItemPlacement.Container;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException or ArgumentException or
            InvalidCastException or OverflowException)
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
            writer.Write(array is null ? ushort.MaxValue : (ushort)Math.Min(array.Length, ushort.MaxValue));
            for (int index = 0; array is not null && index < array.Length && index < ushort.MaxValue; index++)
                WriteValue(writer, type.GetElementType()!, array.GetValue(index), depth + 1);
            return;
        }
        if (typeof(IList).IsAssignableFrom(type))
        {
            var list = (IList?)value;
            writer.Write(list is null ? ushort.MaxValue : (ushort)Math.Min(list.Count, ushort.MaxValue));
            Type element = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            for (int index = 0; list is not null && index < list.Count && index < ushort.MaxValue; index++)
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
        if (data is null) { writer.Write(ushort.MaxValue); return; }
        int count = Math.Min(data.Length, ushort.MaxValue - 1);
        writer.Write((ushort)count);
        for (int index = 0; index < count; index++)
        {
            object? value = data[index];
            switch (value)
            {
                case null: writer.Write((byte)0); break;
                case int number: writer.Write((byte)1); writer.Write(number); break;
                case float number: writer.Write((byte)2); writer.Write(number); break;
                case bool flag: writer.Write((byte)3); writer.Write(flag); break;
                case string text: writer.Write((byte)4); WriteString(writer, text); break;
                default: writer.Write((byte)0); break;
            }
        }
    }

    private static object?[]? ReadCustomData(BinaryReader reader)
    {
        ushort count = reader.ReadUInt16();
        if (count == ushort.MaxValue) return null;
        var data = new object?[count];
        for (int index = 0; index < count; index++)
        {
            data[index] = reader.ReadByte() switch
            {
                1 => reader.ReadInt32(),
                2 => reader.ReadSingle(),
                3 => reader.ReadBoolean(),
                4 => ReadString(reader),
                _ => null
            };
        }
        return data;
    }
    private static void WriteString(BinaryWriter writer, string? value)
    {
        if (value is null) { writer.Write(ushort.MaxValue); return; }
        byte[] bytes = Utf8.GetBytes(value);
        int length = Math.Min(bytes.Length, ushort.MaxValue);
        writer.Write((ushort)length);
        writer.Write(bytes, 0, length);
    }
    private static object? ReadValue(BinaryReader reader, Type type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Item state nesting is too deep.");
        if (type == typeof(string)) return ReadString(reader);
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type.IsEnum) return Enum.ToObject(type, ReadValue(reader, Enum.GetUnderlyingType(type), depth)!);
        if (type == typeof(byte)) return reader.ReadByte();
        if (type == typeof(sbyte)) return reader.ReadSByte();
        if (type == typeof(short)) return reader.ReadInt16();
        if (type == typeof(ushort)) return reader.ReadUInt16();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(uint)) return reader.ReadUInt32();
        if (type == typeof(long)) return reader.ReadInt64();
        if (type == typeof(ulong)) return reader.ReadUInt64();
        if (type == typeof(float)) return reader.ReadSingle();
        if (type == typeof(double)) return reader.ReadDouble();
        if (type == typeof(char)) return reader.ReadChar();
        if (type.IsArray)
        {
            ushort count = reader.ReadUInt16();
            if (count == ushort.MaxValue) return null;
            Type element = type.GetElementType()!;
            Array array = Array.CreateInstance(element, count);
            for (int index = 0; index < count; index++) array.SetValue(ReadValue(reader, element, depth + 1), index);
            return array;
        }
        if (typeof(IList).IsAssignableFrom(type))
        {
            ushort count = reader.ReadUInt16();
            if (count == ushort.MaxValue) return null;
            Type element = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            var list = (IList)Activator.CreateInstance(type)!;
            for (int index = 0; index < count; index++) list.Add(ReadValue(reader, element, depth + 1));
            return list;
        }
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

    private static string? ReadString(BinaryReader reader)
    {
        ushort length = reader.ReadUInt16();
        return length == ushort.MaxValue ? null : Utf8.GetString(reader.ReadBytes(length));
    }
}
