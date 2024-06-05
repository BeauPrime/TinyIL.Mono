using System;

#if UNITY_EDITOR
using Mono.Cecil;
#endif // UNITY_EDITOR

namespace TinyIL {
    /// <summary>
    /// Marks a method to be overwritten with IL instructions from a section of a patch file.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ExternalILAttribute : Attribute {
        public readonly string ExternalName;

        public ExternalILAttribute(string externalName) {
        }
    }

#if UNITY_EDITOR

    /// <summary>
    /// ExternalIL attribute handler.
    /// </summary>
    public static class ExternalILHandler {
        /// <summary>
        /// Handles intrinsic IL substitution.
        /// </summary>
        static public bool Process(MethodDefinition method, ref PatchFileCache patchCache) {

            if (TinyILParser.FindCustomAttribute(method, "ExternalILAttribute", out var externalAttr)) {
                string externalName = externalAttr.ConstructorArguments[0].Value.ToString();
                method.CustomAttributes.Remove(externalAttr);
                string patch = patchCache.FindPatch(externalName);
                TinyILParser.ReplaceWithIL(method, patch);
                return true;
            }

            return false;
        }
    }

#endif // UNITY_EDITOR
}