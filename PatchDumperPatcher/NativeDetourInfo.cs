using MonoMod.RuntimeDetour;
using System;
using System.Reflection;

namespace PatchDumperPatcher
{
    internal class NativeDetourInfo : BaseDetourInfo
    {
        readonly NativeDetour _nativeDetour;

        MethodBase findPinnedMethodFromPtr(IntPtr ptr)
        {
            foreach (MethodBase pinnedMethod in _nativeDetour._Pinned)
            {
                IntPtr nativeStart = pinnedMethod.Pin().GetNativeStart();

                if (nativeStart == ptr)
                {
                    return pinnedMethod;
                }
            }

            return null;
        }

        readonly IntPtr _fromPtr;
        MethodBase _from;
        public override MethodBase From
        {
            get
            {
                if (_from == null)
                {
                    _from = findPinnedMethodFromPtr(_fromPtr);
                }

                return _from;
            }
        }

        readonly IntPtr _toPtr;
        MethodBase _to;
        public override MethodBase To
        {
            get
            {
                if (_to == null)
                {
                    _to = findPinnedMethodFromPtr(_toPtr);
                }

                return _to;
            }
        }

        internal NativeDetourInfo(NativeDetour detour, IntPtr from, IntPtr to, Assembly owner) : base(detour, null, null, owner)
        {
            _nativeDetour = detour;

            _fromPtr = from;
            _toPtr = to;
        }
    }
}
