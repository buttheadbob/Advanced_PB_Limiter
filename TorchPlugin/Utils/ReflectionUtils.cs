using System;
using System.Reflection;

namespace Advanced_PB_Limiter.Utils
{
    public static class ReflectionUtils
    {
        /// <summary>
        /// Gracefully stolen from the original PB Limiter, Credits to SirHamsterAlot and Equinox
        /// </summary>
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        public static MethodInfo StaticMethod(Type t, string name)
        {
            return t.GetMethod(name, StaticFlags) ?? throw new Exception($"Couldn't find method {name} on {t}");
        }
    }
}