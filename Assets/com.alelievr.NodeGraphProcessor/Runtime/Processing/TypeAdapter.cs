using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;

namespace GraphProcessor
{
    /// <summary>
    /// Implement this interface to use the inside your class to define type convertions to use inside the graph.
    /// Example:
    /// <code>
    /// public class CustomConvertions : ITypeAdapter
    /// {
    ///     public static Vector4 ConvertFloatToVector(float from) => new Vector4(from, from, from, from);
    ///     ...
    /// }
    /// </code>
    /// </summary>
    public abstract class ITypeAdapter // TODO: turn this back into an interface when we have C# 8
    {
        public virtual IEnumerable<(Type, Type)> GetIncompatibleTypes() { yield break; }

        public virtual IEnumerable<(Type from, Type to)> ShortCutRequired() { yield break; }
    }

    public static class TypeAdapter
    {
        static Dictionary< (Type from, Type to), Func<object, object> > adapters = new Dictionary< (Type, Type), Func<object, object> >();
        static Dictionary< (Type from, Type to), MethodInfo > adapterMethods = new Dictionary< (Type, Type), MethodInfo >();
        static List< (Type from, Type to)> incompatibleTypes = new List<( Type from, Type to) >();
        static HashSet<(Type from, Type to)> shortCuts = new HashSet<(Type from, Type to)>();

        [System.NonSerialized]
        static bool adaptersLoaded = false;

#if !ENABLE_IL2CPP
        static Func<object, object> ConvertTypeMethodHelper<TParam, TReturn>(MethodInfo method)
        {
            // Convert the slow MethodInfo into a fast, strongly typed, open delegate
            Func<TParam, TReturn> func = (Func<TParam, TReturn>)Delegate.CreateDelegate
                (typeof(Func<TParam, TReturn>), method);

            // Now create a more weakly typed delegate which will call the strongly typed one
            Func<object, object> ret = (object param) => func((TParam)param);
            return ret;
        }
#endif

        static void LoadAllAdapters()
        {
            foreach (Type type in AppDomain.CurrentDomain.GetAllTypes())
            {
                if (typeof(ITypeAdapter).IsAssignableFrom(type))
                {
                    if (type.IsAbstract)
                        continue;
                    
                    var adapter = Activator.CreateInstance(type) as ITypeAdapter;
                    if (adapter != null)
                    {
                        foreach (var types in adapter.GetIncompatibleTypes())
                        {
                            incompatibleTypes.Add((types.Item1, types.Item2));
                            incompatibleTypes.Add((types.Item2, types.Item1));
                        }
                    }
                    
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (method.GetParameters().Length != 1)
                        {
                            Debug.LogError($"Ignoring convertion method {method} because it does not have exactly one parameter");
                            continue;
                        }
                        if (method.ReturnType == typeof(void))
                        {
                            Debug.LogError($"Ignoring convertion method {method} because it does not returns anything");
                            continue;
                        }
                        Type from = method.GetParameters()[0].ParameterType;
                        Type to = method.ReturnType;

                        try {

#if ENABLE_IL2CPP
                            // IL2CPP doesn't suport calling generic functions via reflection (AOT can't generate templated code)
                            Func<object, object> r = (object param) => { return (object)method.Invoke(null, new object[]{ param }); };
#else
                            MethodInfo genericHelper = typeof(TypeAdapter).GetMethod("ConvertTypeMethodHelper", 
                                BindingFlags.Static | BindingFlags.NonPublic);

                            // Now supply the type arguments
                            MethodInfo constructedHelper = genericHelper.MakeGenericMethod(from, to);

                            object ret = constructedHelper.Invoke(null, new object[] { method });
                            var r = (Func<object, object>)ret;
#endif
                            var fromType = method.GetParameters()[0].ParameterType;
                            var toType = method.ReturnType;
                            var tuple = (fromType, toType);
                            adapters.Add(tuple, r);
                            adapterMethods.Add(tuple, method);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"Failed to load the type convertion method: {method}\n{e}");
                        }
                    }

                    if (adapter != null)
                    {
                        foreach (var item in adapter.ShortCutRequired())
                        {
                            shortCuts.Add(item);
                            shortCuts.Add((item.to, item.from));
                        }
                    }
                }
            }

            foreach (var item in shortCuts)
            {
                if (adapters.ContainsKey(item))
                    continue;

                var origin = item.from;
                var destinaiton = item.to;
                var edges = adapters.Keys;
                //TODO: findpath could also find other shortcutrequired in proecess to improve performance
                var path = FindPath(edges, origin, destinaiton);

                if (path?.Any() == true)
                {
                    //compress convertion method from path (from, to) into one method, but this way is somehow bad for performance
                    Func<object, object> convertionFunction = (object from) =>
                    {
                        object current = from;
                        foreach (var type in path.Skip(1))
                        {
                            current = Convert(current, type);
                        }
                        return current;
                    };

                    adapters.Add(item, convertionFunction);
                    adapters[item] = convertionFunction;
                    var pathString = string.Join(" -> ", path.Select(t => t.Name));
                    Debug.Log($"Shortcut convertion method from {origin} to {destinaiton} is created, path: {pathString}");
                }
                else
                {
                    Debug.LogError($"Missing convertion method. There is no way to convert {origin} to {destinaiton}");
                }
            }

            // Ensure that the dictionary contains all the convertions in both ways
            // ex: float to vector but no vector to float
            foreach (var kp in adapters)
            {
                if (!adapters.ContainsKey((kp.Key.to, kp.Key.from)))
                    Debug.LogError($"Missing convertion method. There is one for {kp.Key.from} to {kp.Key.to} but not for {kp.Key.to} to {kp.Key.from}");
            }

            adaptersLoaded = true;
        }

        public static List<Type> FindPath(IEnumerable<(Type from, Type to)> edges, Type origin, Type destination)
        {
            Queue<List<Type>> queue = new Queue<List<Type>>();
            HashSet<Type> visited = new HashSet<Type>();

            queue.Enqueue(new List<Type> { origin });
            visited.Add(origin);

            while (queue.Count > 0)
            {
                List<Type> path = queue.Dequeue();
                Type currentType = path.Last();

                // return if we found the destination
                if (currentType == destination)
                    return path;

                // find all the neighbors of the current type
                foreach (var (from, to) in edges)
                {
                    if (from == currentType && !visited.Contains(to))
                    {
                        List<Type> newPath = new List<Type>(path);
                        newPath.Add(to);

                        queue.Enqueue(newPath);
                        visited.Add(to);
                    }
                }
            }

            // return null if there is no path
            return null;
        }

        public static bool AreIncompatible(Type from, Type to)
        {
            if (incompatibleTypes.Any((k) => k.from == from && k.to == to))
                return true;
            return false;
        }

        public static bool AreAssignable(Type from, Type to)
        {
            if (!adaptersLoaded)
                LoadAllAdapters();
            
            if (AreIncompatible(from, to))
                return false;

            return adapters.ContainsKey((from, to));
        }

        public static bool IsShortCut(Type from, Type to) => shortCuts.Contains((from, to));

        public static Func<object, object> GetConvertionDelegate(Type from , Type to) => adapters[(from, to)];

        public static MethodInfo GetConvertionMethod(Type from, Type to) => adapterMethods[(from, to)];

        public static object Convert(object from, Type targetType)
        {
            if (!adaptersLoaded)
                LoadAllAdapters();

            Func<object, object> convertionFunction;
            if (adapters.TryGetValue((from.GetType(), targetType), out convertionFunction))
                return convertionFunction?.Invoke(from);

            return null;
        }
    }
}