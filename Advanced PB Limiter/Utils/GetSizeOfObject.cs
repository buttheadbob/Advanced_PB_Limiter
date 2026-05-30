using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Sandbox.ModAPI;
using VRage;

namespace Advanced_PB_Limiter.Utils
{
    public static class GetSize
    {
        private static HashSet<string> errorToAvoid = new ();
        private static FastResourceLock _lock = new ();
        
        public static void OfAssembly(IMyGridProgram? instance, out long size, out long time)
        {
            time = 0;
            size = 0;

            _lock.SpinAcquireExclusive();
            long startTracker = Stopwatch.GetTimestamp();
            try
            {
                if (instance == null) return;
                Type programType = instance.GetType();
                Assembly scriptAssembly = programType.Assembly;
                HashSet<object> visited = new();
            
                MeasureFields(programType, instance, ref size, scriptAssembly, visited);
            }
            finally
            {
                time = Stopwatch.GetTimestamp() - startTracker;
                _lock.ReleaseExclusive();
            }
        }
        
        private static void MeasureFields(Type type, object instance, ref long size, Assembly scriptAssembly, HashSet<object> visited)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (errorToAvoid.Contains(field.Name)) continue;
                
                try
                {
                    object? value = field.GetValue(instance);
                    if (value != null && value.GetType() != field.FieldType) continue;
                    
                    OfObject(value, out long _size, scriptAssembly, visited);
                    size += _size;
                }
                catch
                {
                    errorToAvoid.Add(field.Name);
                }
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (errorToAvoid.Contains(property.Name)) continue;
                
                try
                {
                    object? value = property.GetValue(instance);

                    OfObject(value, out long _size, scriptAssembly, visited);
                    size += _size;
                }
                catch
                {
                    errorToAvoid.Add(property.Name);
                }
            }
        }
        
        private static void OfObject(object? obj, out long size, Assembly scriptAssembly, HashSet<object> visited)
        {
            size = 0;
            if (obj == null) return;
            
            Type type = obj.GetType();
            if (type.IsValueType)
            {
                size = Marshal.SizeOf(obj);
                return;
            }
            
            size += IntPtr.Size;
            
            if (type == typeof(string))
            {
                size += Marshal.SizeOf(typeof(IntPtr)) + Encoding.UTF8.GetByteCount((string)obj);
            }
            else if (type.IsArray)
            {
                Array array = (Array)obj;
                foreach (object? item in array)
                {
                    OfObject(item, out long _size, scriptAssembly, visited);
                    size += _size;
                }
            }
            else if (typeof(ICollection).IsAssignableFrom(type))
            {
                ICollection collection = (ICollection)obj;
                if (!type.IsGenericType)
                {
                    size += Marshal.SizeOf(type);
                }

                foreach (object? item in collection)
                {
                    OfObject(item, out long _size, scriptAssembly, visited);
                    size += _size;
                }
            }
            else if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                IEnumerator enumerator = ((IEnumerable)obj).GetEnumerator();
                try
                {
                    while (enumerator.MoveNext())
                    {
                        OfObject(enumerator.Current, out long _size, scriptAssembly, visited);
                        size += _size;
                    }
                }
                finally
                {
                    if (enumerator is IDisposable disposable)
                        disposable.Dispose();
                }
            }
            else if (typeof(IDictionary).IsAssignableFrom(type))
            {
                IDictionary dictionary = (IDictionary)obj;
                foreach (DictionaryEntry entry in dictionary)
                {
                    OfObject(entry.Key, out long keySize, scriptAssembly, visited);
                    OfObject(entry.Value, out long valueSize, scriptAssembly, visited);
                    size += keySize + valueSize;
                }
            }
            else if (type.Assembly == scriptAssembly && visited.Add(obj))
            {
                MeasureFields(type, obj, ref size, scriptAssembly, visited);
            }
        }
    }
}
