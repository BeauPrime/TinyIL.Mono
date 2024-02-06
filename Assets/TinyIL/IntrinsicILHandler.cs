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
    internal static class IntrinsicILHandler {
        /// <summary>
        /// Handles intrinsic IL substitution.
        /// </summary>
        static internal bool Process(MethodDefinition method) {

            if (!TinyILParser.FindCustomAttribute(method, "IntrinsicILAttribute", out var attr)) {
                return false;
            }

            string ilStr = attr.ConstructorArguments[0].Value.ToString();
            method.CustomAttributes.Remove(attr);
            TinyILParser.ReplaceWithIL(method, ilStr);
            return true;
        }
    }

#endif // UNITY_EDITOR
}