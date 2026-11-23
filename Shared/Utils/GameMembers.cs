#nullable enable
using System;
using System.Linq;
using System.Reflection;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Finds the game's private instance members the limiter needs, by name from the members a type declares. A member
    /// that is missing throws, naming it, because the game changed and the host must then stay inactive.
    /// </summary>
    internal static class GameMembers
    {
        public static FieldInfo Field(Type type, string name)
        {
            return type.GetTypeInfo().DeclaredFields.FirstOrDefault(field => !field.IsStatic && field.Name == name)
                   ?? throw Missing(type, name);
        }

        /// <summary>The instance method with exactly these parameter types.</summary>
        public static MethodInfo Method(Type type, string name, params Type[] parameters)
        {
            return type.GetTypeInfo().DeclaredMethods.FirstOrDefault(method => !method.IsStatic && method.Name == name && HasParameters(method, parameters))
                   ?? throw Missing(type, name);
        }

        /// <summary>The one instance method with this name. Throws when there is none, or more than one.</summary>
        public static MethodInfo OnlyMethod(Type type, string name)
        {
            MethodInfo[] found = type.GetTypeInfo().DeclaredMethods.Where(method => !method.IsStatic && method.Name == name).ToArray();
            if (found.Length == 1) return found[0];
            throw found.Length == 0 ? Missing(type, name) : new InvalidOperationException($"{type.Name}.{name} has overloads now, the game has probably changed");
        }

        /// <summary>A member looked up in Init, or an error saying Init has not run.</summary>
        public static T Ready<T>(T? member) where T : class
        {
            return member ?? throw new InvalidOperationException("Advanced PB Limiter used a game member before it was initialized.");
        }

        private static bool HasParameters(MethodInfo method, Type[] parameters)
        {
            ParameterInfo[] declared = method.GetParameters();
            if (declared.Length != parameters.Length) return false;
            for (int index = 0; index < declared.Length; index++)
            {
                if (declared[index].ParameterType != parameters[index]) return false;
            }
            return true;
        }

        private static InvalidOperationException Missing(Type type, string name)
        {
            return new InvalidOperationException($"Could not find {type.Name}.{name}, the game has probably changed");
        }
    }
}
