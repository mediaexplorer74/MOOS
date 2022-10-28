﻿using MOOS.Misc;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MOOS.FS
{
    internal unsafe class TarFS : FileSystem
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct posix_tar_header
        {
            public fixed byte name[100];
            public fixed byte mode[8];
            public fixed byte uid[8];
            public fixed byte gid[8];
            public fixed byte size[12];
            public fixed byte mtime[12];
            public fixed byte chksum[8];
            public byte typeflag;
            public fixed byte linkname[100];
            public fixed byte magic[6];
            public fixed byte version[2];
            public fixed byte uname[32];
            public fixed byte gname[32];
            public fixed byte devmajor[8];
            public fixed byte devminor[8];
            public fixed byte prefix[155];
        };

        [DllImport("*")]
        static extern ulong strtoul_(byte* nptr, byte** endptr, int @base);

        public override List<FileInfo> GetFiles(string Directory)
        {
            ulong sec = 0;
            posix_tar_header hdr;
            posix_tar_header* ptr = &hdr;

            List<FileInfo> list = new List<FileInfo>();
            while (Disk.Instance.Read(sec, 1, (byte*)ptr) && hdr.name[0])
            {
                if (!Validate(ptr)) Panic.Error("This filesystem is not TAR");
                sec++;
                ulong size = strtoul_(hdr.size, null, 8);
                string name = Encoding.UTF8.GetString(hdr.name);
                name.Length -= (name[name.Length - 1] == '/' ? 1 : 0);
                if (IsInDirectory(name, Directory))
                {
                    FileInfo info = new FileInfo();
                    info.Param0 = sec;
                    info.Param1 = size;
                    info.Name = name.Substring(name.LastIndexOf('/') + 1);
                    if (hdr.typeflag == '5') info.Attribute |= FileAttribute.Directory;
                    list.Add(info);
                }
                name.Dispose();
                sec += SizeToSec(size);
            }
            return list;
        }

        public override void Delete(string Name)
        {
            Panic.Error("Delete is not implemented on TarFS");
        }

        public override byte[] ReadAllBytes(string Name)
        {
            string dir = null;
            if(Name.IndexOf('/') == -1)
            {
                dir = "";
            }
            else
            {
                dir = $"{Name.Substring(0, Name.LastIndexOf('/'))}/";
            }
            string fname = Name.Substring(dir.Length);
            byte[] buffer = null;
            List<FileInfo> list = GetFiles(dir);
            for(int i = 0; i < list.Count; i++)
            {
                if (list[i].Name.Equals(fname))
                {
                    buffer = new byte[(uint)SizeToSec(list[i].Param1) * 512];
                    fixed(byte* ptr = buffer)
                    {
                        Disk.Instance.Read(list[i].Param0, (uint)SizeToSec(list[i].Param1), ptr);
                    }
                    buffer.Length = (int)list[i].Param1;
                    //Disposing
                    for (i = 0; i < list.Count; i++)
                    {
                        list[i].Dispose();
                    }
                    list.Dispose();
                    fname.Dispose();
                    return buffer;
                }
            }
            Panic.Error($"{Name} is not found!");
            return buffer;
        }

        private bool Validate(posix_tar_header* hdr) 
        {
            uint chksum = 0;
            for (int i = 7; i >= 0; i--)
            {
                if (hdr->chksum[i] >= 0x30)
                {
                    uint s = hdr->chksum[i] - 0x30;
                    int j = 7 - i;
                    while (j > 2)
                    {
                        s *= 8;
                        j--;
                    }
                    chksum += s;
                }
                hdr->chksum[i] = 0x20;
            }
            uint sum = 0;
            for (int i = 0; i < 512; i++) 
            {
                sum += ((byte*)hdr)[i];
            }
            return chksum == sum;
        }

        public override void WriteAllBytes(string Name, byte[] Content) 
        {
            Panic.Error("WriteAllBytes is not implemented on TarFS");
        }

        public override void Format()
        {
            Panic.Error("Format is not implemented on TarFS");
        }
    }
}
