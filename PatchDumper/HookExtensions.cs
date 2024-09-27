using MonoMod.RuntimeDetour;

namespace PatchDumper
{
    public static class HookExtensions
    {
        public static bool TryGetUnderlyingDetour(this IDetour detour, out Detour underlying)
        {
            switch (detour)
            {
                case Hook hook:
                    underlying = hook.Detour;
                    break;
                case ILHook ilHook:
                    underlying = ilHook._Ctx.Detour;
                    break;
                default:
                    underlying = null;
                    break;
            }

            return underlying != null;
        }
    }
}
