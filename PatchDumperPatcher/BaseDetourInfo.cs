using MonoMod.RuntimeDetour;
using System;
using System.Reflection;

namespace PatchDumperPatcher
{
    public class BaseDetourInfo
    {
        public IDetour Detour { get; }

        public Assembly Owner { get; }

        public virtual MethodBase From { get; }

        public virtual MethodBase To { get; }

        public BaseDetourInfo(IDetour detour, MethodBase from, MethodBase to, Assembly owner)
        {
            Detour = detour ?? throw new ArgumentNullException(nameof(detour));
            Owner = owner;
            From = from;
            To = to;
        }
    }
}
