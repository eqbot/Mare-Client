﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Penumbra.Interop.Structs;

namespace MareSynchronos.Interop
{
    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct Weapon
    {
        [FieldOffset(0x18)] public IntPtr Parent;
        [FieldOffset(0x20)] public IntPtr NextSibling;
        [FieldOffset(0x28)] public IntPtr PreviousSibling;
        [FieldOffset(0xA8)] public WeaponDrawObject* WeaponRenderModel;
    }

    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct WeaponDrawObject
    {
        [FieldOffset(0x00)] public RenderModel* RenderModel;
    }

    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct HumanExt
    {
        [FieldOffset(0x0)] public Human Human;
        [FieldOffset(0x9E8)] public ResourceHandle* Decal;
        [FieldOffset(0x9F0)] public ResourceHandle* LegacyBodyDecal;
    }
}