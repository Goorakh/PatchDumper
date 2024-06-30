using System;

namespace PatchDumperPatcher
{
    static class Util
    {
        public static void AppendDelegate<TDelegate>(ref TDelegate del, TDelegate add) where TDelegate : Delegate
        {
            del = (TDelegate)Delegate.Combine(del, add);
        }

        public static void RemoveDelegate<TDelegate>(ref TDelegate del, TDelegate remove, bool all) where TDelegate : Delegate
        {
            del = (TDelegate)(all ? Delegate.RemoveAll(del, remove) : Delegate.Remove(del, remove));
        }
    }
}
