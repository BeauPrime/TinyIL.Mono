using System;

#if UNITY_EDITOR
using Mono.Cecil;
#endif // UNITY_EDITOR

namespace TinyIL {
    /// <summary>
    /// Marks a method to be overwritten with the specified IL instructions.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class IntrinsicILAttribute : Attribute {
        public readonly string Instructions;

        public IntrinsicILAttribute(string instructions) { 

        }
    }

#if UNITY_EDITOR

    /// <summary>
    /// IntrinsicIL attribute handler.
    /// </summary>
    public static class IntrinsicILHandler {
        /// <summary>
        /// Handles intrinsic IL substitution.
        /// </summary>
        static public bool Process(MethodDefinition method, ref PatchFileCache patchCache) {

            if (TinyILParser.FindCustomAttribute(method, "IntrinsicILAttribute", out var intrinsicAttr)) {
                string ilStr = intrinsicAttr.ConstructorArguments[0].Value.ToString();
                method.CustomAttributes.Remove(intrinsicAttr);
                TinyILParser.ReplaceWithIL(method, ilStr);
                return true;
            }

            return false;
        }
    }

#endif // UNITY_EDITOR
}