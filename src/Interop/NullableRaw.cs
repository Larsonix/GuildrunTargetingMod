using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppEmber.Balancing.SimulationBridge;
using Il2CppEmber.Scopes.Battle.Board.Data;
using Il2CppEmber.Scopes.GameRun.GameRegistry.Data.Characters;
using Il2CppEmber.Simulation.Core.State.Components;
using Il2CppEmber.Simulation.Core.State.Entities;
using Il2CppPhoton.Deterministic;

namespace GuildrunTargetingMod.Interop;

/// <summary>
/// Reads nullable fields out of the game's value types by walking native memory, instead of
/// calling the generated properties that normally expose them.
/// </summary>
/// <remarks>
/// The generated getter for a nullable field boxes the value before handing it back, and boxing
/// an empty nullable produces a null pointer that crashes the runtime rather than returning
/// nothing. Since an empty target and an empty tile are the ordinary case here, every one of
/// these reads has to go around the getter : ask the runtime where the field sits, then read the
/// "has a value" flag and the payload in place. Offsets are resolved once at startup and printed
/// in the boot log, so a game update that moves a field is visible immediately.
/// </remarks>
internal static unsafe class NullableRaw
{

    public readonly struct FieldRef
    {
        internal readonly IntPtr ClassPointer;
        internal readonly int Offset;
        internal readonly string Name;

        internal FieldRef(IntPtr classPointer, int offset, string name)
        {
            ClassPointer = classPointer;
            Offset = offset;
            Name = name;
        }
    }

    private static readonly int BoxHeaderSize = IntPtr.Size * 2;
    private static int _entityCharacterOffset;
    private static int _entityTransformOffset;
    private static int _characterTargetOffset;
    private static int _characterPreparedOffset;
    private static int _transformVisualOffset;
    private static int _tileHeroOffset;
    private static int _tileEnemyOffset;
    private static int _preparedTargetOffset;
    private static bool _ready;

    public static string LayoutDiagnostic { get; private set; }

    public static void Initialize()
    {
        _entityCharacterOffset = FieldOffset<Entity>("Character");
        _entityTransformOffset = FieldOffset<Entity>("Transform");
        _characterTargetOffset = FieldOffset<CharacterComponent>("<TargetId>k__BackingField");
        _characterPreparedOffset = FieldOffset<CharacterComponent>("<PreparedAttack>k__BackingField");
        _transformVisualOffset = FieldOffset<TransformComponent>("<VisualOffset>k__BackingField");
        _tileHeroOffset = FieldOffset<TileInfo>("<HeroId>k__BackingField");
        _tileEnemyOffset = FieldOffset<TileInfo>("<EnemyId>k__BackingField");
        _preparedTargetOffset = FieldOffset<PreparedAttack>("<TargetId>k__BackingField") - BoxHeaderSize;

        var entityLayout = NullableLayout<EntityId>.Describe();
        var heroLayout = NullableLayout<HeroId>.Describe();
        var enemyLayout = NullableLayout<EnemyId>.Describe();
        var attackLayout = NullableLayout<PreparedAttack>.Describe();
        var vectorLayout = NullableLayout<FPVector3>.Describe();
        LayoutDiagnostic = string.Join("; ", entityLayout, heroLayout, enemyLayout, attackLayout, vectorLayout);
        _ready = true;
    }

    public static bool TryReadEffectiveTarget(Entity entity, out EntityId target)
    {
        EnsureReady();
        target = default;
        if (entity == null) return false;

        // A unit with a wind-up already committed is aiming at that attack's target, not at
        // whatever it would pick right now, so the prepared attack is checked first and the plain
        // target is the fallback.
        byte* character = (byte*)entity.Pointer + _entityCharacterOffset;
        if (ReadNullable(character + ValueFieldRelative<CharacterComponent>(_characterPreparedOffset), out PreparedAttack attack))
        {
            target = *(EntityId*)((byte*)&attack + _preparedTargetOffset);
            return true;
        }
        return ReadNullable(character + ValueFieldRelative<CharacterComponent>(_characterTargetOffset), out target);
    }

    public static bool TryReadTileHero(TileInfo tile, out HeroId id)
    {
        EnsureReady();
        id = default;
        if (tile == null) return false;
        // An empty cell is the common case, and that is exactly the case the generated getter
        // throws on, so read the field where it lies.
        return ReadNullable((byte*)tile.Pointer + _tileHeroOffset, out id);
    }

    public static bool TryReadTileEnemy(TileInfo tile, out EnemyId id)
    {
        EnsureReady();
        id = default;
        if (tile == null) return false;
        // Same reason as the hero read above ; an enemy id has the same single-guid payload.
        return ReadNullable((byte*)tile.Pointer + _tileEnemyOffset, out id);
    }

    public static bool TryReadVisualOffset(Entity entity, out FPVector3 value)
    {
        EnsureReady();
        value = default;
        if (entity == null) return false;
        // The offset lives one value type deeper, inside the transform component, and it is
        // cleared whenever a unit is not mid-travel. Walk both in place rather than through two
        // getters that both throw on the empty case.
        byte* transform = (byte*)entity.Pointer + _entityTransformOffset;
        return ReadNullable(transform + ValueFieldRelative<TransformComponent>(_transformVisualOffset), out value);
    }

    public static bool HasNullableAt<T>(Il2CppObjectBase owner, string fieldName) where T : unmanaged
    {
        if (owner == null) return false;
        int offset = InstanceFieldOffset(owner, fieldName);
        return NullableLayout<T>.HasValue((byte*)owner.Pointer + offset);
    }

    /// <summary>
    /// Reads a private reference-typed field, such as an array the game never exposes.
    /// </summary>
    /// <remarks>
    /// Ordinary reflection cannot do this. The generated interop type only carries members the
    /// game made public, so asking it for a private field finds nothing and hands back null,
    /// which reads exactly like an empty field and is not the same thing at all. That silence
    /// shipped once : every compound condition looked like it held no conditions, so no item
    /// whose rule is written as a compound was ever recognised, while single-condition relics
    /// worked perfectly and made it look like the feature was fine.
    /// </remarks>
    public static Il2CppReferenceArray<T> ReadReferenceArrayAt<T>(Il2CppObjectBase owner, string fieldName)
        where T : Il2CppObjectBase
    {
        if (owner == null) return null;
        int offset = InstanceFieldOffset(owner, fieldName);
        IntPtr value = *(IntPtr*)((byte*)owner.Pointer + offset);
        return value == IntPtr.Zero ? null : new Il2CppReferenceArray<T>(value);
    }

    /// <summary>Reads a private reference-typed field and wraps it as <typeparamref name="T"/>.</summary>
    /// <remarks>Same reason as <see cref="ReadReferenceArrayAt{T}"/> : reflection cannot see a
    /// private game field and returns null instead of failing, which reads as "empty".</remarks>
    public static T ReadObjectAt<T>(Il2CppObjectBase owner, string fieldName) where T : Il2CppObjectBase
    {
        if (owner == null) return null;
        int offset = InstanceFieldOffset(owner, fieldName);
        IntPtr value = *(IntPtr*)((byte*)owner.Pointer + offset);
        return value == IntPtr.Zero ? null : (T)Activator.CreateInstance(typeof(T), value);
    }

    public static bool TryReadEntityIdAt(Il2CppObjectBase owner, string fieldName, out EntityId id) =>
        TryReadNullableAt(owner, fieldName, out id);

    /// <summary>Reads a nullable value-typed field at its offset, whatever the payload.</summary>
    /// <remarks>
    /// Same reason as every other read here : the generated getter boxes the value first, and
    /// boxing an empty nullable hands back a null pointer that brings the runtime down. An empty
    /// item slot and an unequipped item are both ordinary, so the empty case is the common one.
    /// </remarks>
    public static bool TryReadNullableAt<T>(Il2CppObjectBase owner, string fieldName, out T value)
        where T : unmanaged
    {
        value = default;
        if (owner == null) return false;
        int offset = InstanceFieldOffset(owner, fieldName);
        return ReadNullable((byte*)owner.Pointer + offset, out value);
    }

    public static FieldRef ResolveField<T>(Il2CppObjectBase owner, string fieldName) where T : unmanaged
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        IntPtr klass = IL2CPP.il2cpp_object_get_class(owner.Pointer);
        if (klass == IntPtr.Zero) throw new TypeLoadException("il2cpp_object_get_class returned zero");
        int offset = InstanceFieldOffset(owner, fieldName);
        return new FieldRef(klass, offset, fieldName);
    }

    public static bool TryRead<T>(Il2CppObjectBase owner, FieldRef field, out T value) where T : unmanaged
    {
        value = default;
        if (owner == null) return false;
        IntPtr actualClass = IL2CPP.il2cpp_object_get_class(owner.Pointer);
        if (actualClass != field.ClassPointer)
            return TryReadNullableAt(owner, field.Name, out value);
        return ReadNullable((byte*)owner.Pointer + field.Offset, out value);
    }

    /// <summary>
    /// Whether two raw ids read out of the game hold the same bytes.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than reached for through EqualityComparer, and the reason is a trap
    /// rather than a preference. These id types arrive as generated interop proxies, and a proxy
    /// struct does not necessarily carry the IEquatable the game's own source declares. When it
    /// does not, EqualityComparer falls back to ValueType.Equals, which BOXES BOTH OPERANDS : two
    /// allocations per comparison, on a path that is being changed specifically to stop allocating.
    /// The optimisation would have cost more than the thing it replaced, silently, and nothing
    /// about the code would have looked wrong.
    ///
    /// Comparing the bytes needs no interface, no operator and no interop call, and these ids are
    /// constrained unmanaged, so their bytes are the whole of their identity.
    /// </remarks>
    public static bool SameRawId<T>(in T left, in T right) where T : unmanaged
    {
        fixed (T* a = &left)
        fixed (T* b = &right)
        {
            byte* x = (byte*)a;
            byte* y = (byte*)b;
            for (int i = 0; i < sizeof(T); i++)
                if (x[i] != y[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Every instance field on <paramref name="owner"/>'s real class whose type is
    /// <typeparamref name="TField"/>, found by type rather than by name.
    /// </summary>
    /// <remarks>
    /// This is what keeps a feature free of hero names. The game stores the same kind of thing
    /// under a different name on every class that has one : an ability's area of effect is
    /// _aoeEntry on the generic action, _regularAoeEntry on most heroes, and _stallAoeEntry or
    /// _protectorAoeEntry or _seasonedAoeEntry beside it on three of them. Binding those names
    /// would be ten bindings into hero specific classes, which is the most fragile surface in the
    /// game and the one thing this mod is built to avoid. Asking "the field whose type is this"
    /// has no hero in it at all, so a hero added in a patch is covered without a change here.
    ///
    /// Statics are skipped : their offset is into the class's own storage, not into an instance,
    /// and reading one as an instance field would be reading a random part of the object.
    /// </remarks>
    public static List<TField> ReadFieldsOfType<TField>(Il2CppObjectBase owner)
        where TField : Il2CppObjectBase
    {
        var found = new List<TField>(2);
        if (owner == null) return found;
        IntPtr klass = IL2CPP.il2cpp_object_get_class(owner.Pointer);
        if (klass == IntPtr.Zero) throw new TypeLoadException("il2cpp_object_get_class returned zero");
        // The type being looked for has to have introduced itself to the runtime before its class
        // pointer exists, and nothing here has necessarily touched it yet. Reading the store first
        // and finding a zero would look exactly like "this action has no such field", which is the
        // silent shape of failure this whole file exists to avoid. Run the type's own initializer,
        // then insist on a real pointer.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TField).TypeHandle);
        IntPtr wanted = Il2CppClassPointerStore<TField>.NativeClassPtr;
        if (wanted == IntPtr.Zero)
            throw new TypeLoadException("native class pointer unavailable for " + typeof(TField).Name);

        foreach (int offset in FieldOffsetsOfType(klass, wanted))
        {
            IntPtr value = *(IntPtr*)((byte*)owner.Pointer + offset);
            if (value != IntPtr.Zero) found.Add((TField)Activator.CreateInstance(typeof(TField), value));
        }
        return found;
    }

    // ECMA-335 FieldAttributes.Static. The runtime reports .NET field flags unchanged.
    private const int FieldAttributeStatic = 0x0010;

    private static readonly System.Collections.Generic.Dictionary<(IntPtr, IntPtr), int[]> TypedFieldCache = new();

    // Walks the class and its bases once per (class, field type) pair and remembers the answer.
    // The classes here are authored data whose shape cannot change while the game is running, and
    // a scan per unit per frame would be the most expensive thing the mod does.
    private static int[] FieldOffsetsOfType(IntPtr klass, IntPtr wanted)
    {
        if (TypedFieldCache.TryGetValue((klass, wanted), out int[] cached)) return cached;
        var offsets = new List<int>(2);
        IntPtr search = klass;
        while (search != IntPtr.Zero)
        {
            IntPtr iterator = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(search, ref iterator)) != IntPtr.Zero)
            {
                if ((IL2CPP.il2cpp_field_get_flags(field) & FieldAttributeStatic) != 0) continue;
                IntPtr type = IL2CPP.il2cpp_field_get_type(field);
                if (type == IntPtr.Zero) continue;
                if (IL2CPP.il2cpp_class_from_il2cpp_type(type) != wanted) continue;
                offsets.Add(checked((int)IL2CPP.il2cpp_field_get_offset(field)));
            }
            search = IL2CPP.il2cpp_class_get_parent(search);
        }
        int[] result = offsets.ToArray();
        TypedFieldCache[(klass, wanted)] = result;
        return result;
    }

    private static int InstanceFieldOffset(Il2CppObjectBase owner, string fieldName)
    {
        // Ask the native object what it is, never the managed wrapper. A property declared as an
        // interface hands back a wrapper whose managed type is that interface, which has no
        // fields at all, so looking the field up on it finds nothing. The object's own header
        // knows its real class.
        IntPtr klass = IL2CPP.il2cpp_object_get_class(owner.Pointer);
        if (klass == IntPtr.Zero) throw new TypeLoadException("il2cpp_object_get_class returned zero");
        if (OffsetCache.TryGetValue((klass, fieldName), out int cached)) return cached;
        int offset = FieldOffset(klass, fieldName);
        OffsetCache[(klass, fieldName)] = offset;
        return offset;
    }

    private static bool ReadNullable<T>(byte* nullableData, out T value) where T : unmanaged
    {
        if (!NullableLayout<T>.HasValue(nullableData))
        {
            value = default;
            return false;
        }
        value = *(T*)(nullableData + NullableLayout<T>.ValueOffset);
        return true;
    }

    private static int ValueFieldRelative<T>(int boxedFieldOffset) where T : new() => boxedFieldOffset - BoxHeaderSize;

    private static int FieldOffset<T>(string fieldName) => FieldOffset(Il2CppClassPointerStore<T>.NativeClassPtr, fieldName);

    private static readonly System.Collections.Generic.Dictionary<(IntPtr, string), int> OffsetCache = new();

    private static int FieldOffset(IntPtr klass, string fieldName)
    {
        if (klass == IntPtr.Zero) throw new TypeLoadException("native class pointer unavailable for " + fieldName);
        IntPtr field = IntPtr.Zero;
        IntPtr search = klass;
        while (search != IntPtr.Zero && field == IntPtr.Zero)
        {
            field = IL2CPP.il2cpp_class_get_field_from_name(search, fieldName);
            search = IL2CPP.il2cpp_class_get_parent(search);
        }
        if (field == IntPtr.Zero) throw new MissingFieldException(fieldName);
        return checked((int)IL2CPP.il2cpp_field_get_offset(field));
    }

    private static void EnsureReady()
    {
        if (!_ready) throw new InvalidOperationException("NullableRaw.Initialize was not called");
    }

    private static class NullableLayout<T> where T : unmanaged
    {
        public static readonly int HasOffset;
        public static readonly int ValueOffset;

        static NullableLayout()
        {
            IntPtr klass = Il2CppClassPointerStore<Il2CppSystem.Nullable<T>>.NativeClassPtr;
            HasOffset = FieldOffset(klass, "hasValue") - BoxHeaderSize;
            ValueOffset = FieldOffset(klass, "value") - BoxHeaderSize;
            if (HasOffset < 0 || ValueOffset < 0)
                throw new InvalidOperationException("nullable offsets did not resolve to unboxed data");
        }

        public static bool HasValue(byte* data) => *(bool*)(data + HasOffset);
        public static string Describe() => $"Nullable<{typeof(T).Name}>[has={HasOffset},value={ValueOffset},payload={sizeof(T)}]";
    }
}
