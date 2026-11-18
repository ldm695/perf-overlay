using System.Runtime.InteropServices;

namespace PerfOverlay.Metrics;

internal static class DxCoreAdapterEnumerator
{
    public static string? LastError { get; private set; }
    private static readonly Guid FactoryId = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid ListId = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid AdapterId = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    private static readonly Guid D3D12Graphics = new("b69eb219-3ded-4464-979f-a00bd4687006");

    public static IReadOnlyList<Adapter> Enumerate()
    {
        LastError = null;
        IDXCoreAdapterFactory? factory = null;
        IDXCoreAdapterList? list = null;
        try
        {
            var iid = FactoryId;
            Marshal.ThrowExceptionForHR(DXCoreCreateAdapterFactory(ref iid, out factory));
            iid = ListId;
            var attr = D3D12Graphics;
            var attributeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            try
            {
                Marshal.StructureToPtr(attr, attributeBuffer, false);
                Marshal.ThrowExceptionForHR(factory.CreateAdapterList(1, attributeBuffer, ref iid, out list));
            }
            finally { Marshal.FreeHGlobal(attributeBuffer); }
            var result = new List<Adapter>();
            iid = AdapterId;
            for (uint i = 0; i < list.GetAdapterCount(); i++)
            {
                if (list.GetAdapter(i, ref iid, out var adapter) < 0 || adapter is null) continue;
                try
                {
                    try
                    {
                        var luid = ReadProperty<Luid>(adapter, Property.InstanceLuid);
                        var hardwareId = ReadProperty<HardwareId>(adapter, Property.HardwareID);
                        var integrated = ReadProperty<byte>(adapter, Property.IsIntegrated) != 0;
                        var detachable = ReadProperty<byte>(adapter, Property.IsDetachable) != 0;
                        var dedicatedMemory = ReadProperty<ulong>(adapter, Property.DedicatedAdapterMemory);
                        var sharedMemory = ReadProperty<ulong>(adapter, Property.SharedSystemMemory);
                        var hardware = ReadProperty<byte>(adapter, Property.IsHardware) != 0;
                        result.Add(new Adapter(luid, hardwareId.VendorId, hardwareId.DeviceId, integrated, detachable, dedicatedMemory, sharedMemory, hardware));
                    }
                    catch (Exception ex) { LastError = $"Adapter {i}: {ex.GetType().Name} 0x{ex.HResult:x8}: {ex.Message}"; }
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            return result;
        }
        catch (Exception ex) { LastError = $"DXCore: {ex.GetType().Name} 0x{ex.HResult:x8}: {ex.Message}"; return Array.Empty<Adapter>(); }
        finally
        {
            if (list is not null) Marshal.ReleaseComObject(list);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    public static Adapter? GetByLuid(Luid luid)
    {
        IDXCoreAdapterFactory? factory = null;
        IDXCoreAdapter? adapter = null;
        try
        {
            var iid = FactoryId;
            Marshal.ThrowExceptionForHR(DXCoreCreateAdapterFactory(ref iid, out factory));
            iid = AdapterId;
            Marshal.ThrowExceptionForHR(factory.GetAdapterByLuid(ref luid, ref iid, out adapter));
            var id = ReadProperty<HardwareId>(adapter!, Property.HardwareID);
            return new Adapter(luid, id.VendorId, id.DeviceId,
                ReadProperty<byte>(adapter!, Property.IsIntegrated) != 0,
                ReadProperty<byte>(adapter!, Property.IsDetachable) != 0,
                ReadProperty<ulong>(adapter!, Property.DedicatedAdapterMemory),
                ReadProperty<ulong>(adapter!, Property.SharedSystemMemory),
                ReadProperty<byte>(adapter!, Property.IsHardware) != 0);
        }
        catch (Exception ex) { LastError = $"GetAdapterByLuid: 0x{ex.HResult:x8}: {ex.Message}"; return null; }
        finally
        {
            if (adapter is not null) Marshal.ReleaseComObject(adapter);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static T ReadProperty<T>(IDXCoreAdapter adapter, Property property) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.ThrowExceptionForHR(adapter.GetProperty(property, (nuint)size, buffer));
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("dxcore.dll", ExactSpelling = true)] private static extern int DXCoreCreateAdapterFactory(ref Guid iid, out IDXCoreAdapterFactory factory);
    private enum Property : uint { InstanceLuid = 0, HardwareID = 3, DedicatedAdapterMemory = 7, SharedSystemMemory = 9, IsHardware = 11, IsIntegrated = 12, IsDetachable = 13 }
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint LowPart; public int HighPart; public override readonly string ToString() => $"luid_0x{HighPart:x8}_0x{LowPart:x8}"; }
    [StructLayout(LayoutKind.Sequential)] private struct HardwareId { public uint VendorId, DeviceId, SubsystemId, Revision; }
    internal sealed record Adapter(Luid Luid, uint VendorId, uint DeviceId, bool IsIntegrated, bool IsDetachable, ulong DedicatedMemoryBytes, ulong SharedMemoryBytes, bool IsHardware);

    [ComImport, Guid("78ee5945-c36e-4b13-a669-005dd11c0f06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXCoreAdapterFactory
    {
        int CreateAdapterList(uint count, IntPtr attributes, ref Guid iid, out IDXCoreAdapterList list);
        int GetAdapterByLuid([In] ref Luid luid, ref Guid iid, out IDXCoreAdapter adapter);
    }
    [ComImport, Guid("526c7776-40e9-459b-b711-f32ad76dfc28"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXCoreAdapterList { int GetAdapter(uint index, ref Guid iid, out IDXCoreAdapter adapter); uint GetAdapterCount(); }
    [ComImport, Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXCoreAdapter
    {
        [return: MarshalAs(UnmanagedType.I1)] bool IsValid();
        [return: MarshalAs(UnmanagedType.I1)] bool IsAttributeSupported(ref Guid attribute);
        [return: MarshalAs(UnmanagedType.I1)] bool IsPropertySupported(Property property);
        int GetProperty(Property property, nuint size, IntPtr data);
    }
}
