using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace GuildrunTargetingMod.Ui;

// Finds the game's own objects and reads the fields it does not expose.
//
// The mod borrows the game's camera, fonts, colours, tile sprites and buttons rather than
// shipping its own, so that it looks like part of the game and keeps looking like it when the
// game's art changes. Most of what it needs is a private serialized field, hence the reflection.
internal static class RuntimeDiscovery
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static List<T> FindAll<T>() where T : UnityEngine.Object
    {
        var result = new List<T>();
        // Includes objects that are loaded but switched off, which the scene-only search does
        // not. Half the UI the mod needs is a pooled panel sitting inactive until it is used.
        Il2CppReferenceArray<UnityEngine.Object> objects = Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>());
        if (objects == null) return result;
        for (int i = 0; i < objects.Length; i++)
        {
            UnityEngine.Object value = objects[i];
            if (value == null) continue;
            T cast = value.TryCast<T>();
            if (cast != null) result.Add(cast);
        }
        return result;
    }

    // The one that is really in the scene. The search above also returns prefabs and other
    // loaded assets, which look identical from managed code and are not what any caller wants.
    public static T FindLive<T>() where T : Component
    {
        List<T> all = FindAll<T>();
        for (int i = 0; i < all.Count; i++)
        {
            T value = all[i];
            if (value != null && value.gameObject.scene.IsValid()) return value;
        }
        return null;
    }

    public static T ReadField<T>(object owner, string fieldName) where T : class
    {
        if (owner == null) return null;
        Type type = owner.GetType();
        while (type != null)
        {
            FieldInfo field = type.GetField(fieldName, All);
            if (field != null) return field.GetValue(owner) as T;
            // A game field can arrive on the managed side as a property carrying the field's own
            // name, so both have to be tried before giving up on a name.
            PropertyInfo property = type.GetProperty(fieldName, All);
            if (property != null) return property.GetValue(owner) as T;
            type = type.BaseType;
        }
        return null;
    }

    // Writes a serialized field the game never exposes. The mod builds its own tooltip anchor at
    // runtime, and the game only ever fills those fields in the editor.
    public static bool WriteField(object owner, string fieldName, object value)
    {
        if (owner == null) return false;
        Type type = owner.GetType();
        while (type != null)
        {
            FieldInfo field = type.GetField(fieldName, All);
            if (field != null) { field.SetValue(owner, value); return true; }
            PropertyInfo property = type.GetProperty(fieldName, All);
            if (property != null && property.CanWrite) { property.SetValue(owner, value); return true; }
            type = type.BaseType;
        }
        return false;
    }

    // The object's real class, asked of the native object rather than of the managed wrapper. A
    // wrapper reports the type it was declared as, which for anything reached through an interface
    // is that interface, and the interface is the same for every one of them.
    //
    // Careful here. The name comes back as a plain C string, not as one of the runtime's own string
    // objects. Handing it to the runtime's string converter instead reads a length out of the
    // middle of some characters and then tries to allocate that many bytes, which is an
    // out-of-memory failure a few frames later and nothing that looks like the mistake it was.
    // Measured in play on 2026-08-17 : thirty checks in a row, and the feature switched itself off.
    public static string NativeClassName(Il2CppObjectBase value)
    {
        if (value == null) return string.Empty;
        IntPtr klass = IL2CPP.il2cpp_object_get_class(value.Pointer);
        if (klass == IntPtr.Zero) return string.Empty;
        IntPtr name = IL2CPP.il2cpp_class_get_name(klass);
        return name == IntPtr.Zero ? string.Empty : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(name) ?? string.Empty;
    }

    // The full path of an object in the scene, for the log and the census file. When a player
    // reports that something did not appear, this is what says where the mod was looking.
    public static string HierarchyPath(Transform transform)
    {
        if (transform == null) return "unavailable";
        string path = transform.name;
        for (Transform p = transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
