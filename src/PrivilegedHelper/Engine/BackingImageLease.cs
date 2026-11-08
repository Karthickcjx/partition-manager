using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PartitionManager.Core.Models;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>Read-only native correlation between the exact image path and attached device.</summary>
internal sealed class BackingImageLease(SafeFileHandle virtualHandle, SafeFileHandle fileHandle) : IDisposable
{
    public static BackingImageLease Open(DiskInfo disk)
    {
        var path = disk.VirtualImagePath;
        if (!disk.IsVirtual || string.IsNullOrWhiteSpace(path)) throw new IOException("Not a file-backed virtual disk.");
        RejectReparseAncestors(path);
        // Pin the file without delete-sharing, preventing rename/replacement during the request.
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            var storage = new StorageType { DeviceId = path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ? 3u : 2u,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B") };
            var parameters = new OpenParameters { Version = 2, GetInfoOnly = 1, ReadOnly = 1 };
            var rc = OpenVirtualDisk(ref storage,path,0 /* version 2 uses GetInfoOnly */,0,ref parameters,out var handle);
            if(rc!=0) { handle.Dispose(); throw new Win32Exception((int)rc); }
            try
            {
                var size=1024; var buffer=new char[size/2];
                rc=GetVirtualDiskPhysicalPath(handle,ref size,buffer);
                if(rc!=0) throw new Win32Exception((int)rc);
                var physical=new string(buffer).TrimEnd('\0');
                if(!string.Equals(physical,$@"\\.\PhysicalDrive{disk.Number}",StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Virtual Disk API reports a different attached disk.");
                RejectReparseAncestors(path);
                return new(handle,file);
            }
            catch { handle.Dispose(); throw; }
        }
        catch { file.Dispose(); throw; }
    }

    internal static void RejectReparseAncestors(string path)
    {
        if ((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("Image is a reparse point.");
        for(var dir=new FileInfo(path).Directory;dir is not null;dir=dir.Parent)
            if((dir.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Image has a reparse ancestor.");
    }

    public void Dispose() { virtualHandle.Dispose(); fileHandle.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct StorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Sequential)] private struct OpenParameters { public uint Version; public int GetInfoOnly; public int ReadOnly; public Guid ResiliencyGuid; }
    [DllImport("virtdisk.dll",CharSet=CharSet.Unicode)] private static extern uint OpenVirtualDisk(ref StorageType storage,string path,uint access,uint flags,ref OpenParameters parameters,out SafeFileHandle handle);
    [DllImport("virtdisk.dll",CharSet=CharSet.Unicode)] private static extern uint GetVirtualDiskPhysicalPath(SafeFileHandle handle,ref int size,[Out] char[] path);
}
