﻿using System;
using MareSynchronos.API;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System.Runtime.InteropServices;
using MareSynchronos.Utils;
using Penumbra.GameData.ByteString;

namespace MareSynchronos.Models
{
    internal class PlayerOrRelatedObject
    {
        private readonly Func<IntPtr> getAddress;

        public unsafe Character* Character => (Character*)Address;

        private string _name;

        public ObjectKind ObjectKind { get; }
        public IntPtr Address { get; set; }
        public IntPtr DrawObjectAddress { get; set; }

        private IntPtr CurrentAddress => getAddress.Invoke();

        public PlayerOrRelatedObject(ObjectKind objectKind, IntPtr address, IntPtr drawObjectAddress, Func<IntPtr> getAddress)
        {
            ObjectKind = objectKind;
            Address = address;
            DrawObjectAddress = drawObjectAddress;
            this.getAddress = getAddress;
            _name = string.Empty;
        }

        public byte[] EquipSlotData { get; set; } = new byte[40];
        public byte[] CustomizeData { get; set; } = new byte[26];

        public bool HasUnprocessedUpdate { get; set; } = false;
        public bool IsProcessing { get; set; } = false;

        public unsafe void CheckAndUpdateObject()
        {
            var curPtr = CurrentAddress;
            if (curPtr != IntPtr.Zero)
            {
                var chara = (Character*)curPtr;
                bool addr = Address == IntPtr.Zero || Address != curPtr;
                bool equip = CompareAndUpdateByteData(chara->EquipSlotData, chara->CustomizeData);
                bool drawObj = (chara->GameObject.DrawObject != null && (IntPtr)chara->GameObject.DrawObject != DrawObjectAddress);
                var name = new Utf8String(chara->GameObject.Name).ToString();
                bool nameChange = (name != _name);
                if (addr || equip || drawObj || nameChange)
                {
                    _name = name;
                    Logger.Verbose(ObjectKind + " Changed: " + _name + ", now: " + curPtr + ", " + (IntPtr)chara->GameObject.DrawObject);

                    Address = curPtr;
                    DrawObjectAddress = (IntPtr)chara->GameObject.DrawObject;
                    HasUnprocessedUpdate = true;
                }
            }
            else
            {
                if (Address != IntPtr.Zero || DrawObjectAddress != IntPtr.Zero)
                {
                    Address = IntPtr.Zero;
                    DrawObjectAddress = IntPtr.Zero;
                    HasUnprocessedUpdate = true;
                }

                Address = IntPtr.Zero;
                DrawObjectAddress = IntPtr.Zero;
            }
        }

        private unsafe bool CompareAndUpdateByteData(byte* equipSlotData, byte* customizeData)
        {
            bool hasChanges = false;
            for (int i = 0; i < EquipSlotData.Length; i++)
            {
                var data = Marshal.ReadByte((IntPtr)equipSlotData, i);
                if (EquipSlotData[i] != data)
                {
                    EquipSlotData[i] = data;
                    hasChanges = true;
                }
            }

            for (int i = 0; i < CustomizeData.Length; i++)
            {
                var data = Marshal.ReadByte((IntPtr)customizeData, i);
                if (CustomizeData[i] != data)
                {
                    CustomizeData[i] = data;
                    hasChanges = true;
                }
            }

            return hasChanges;
        }
    }
}