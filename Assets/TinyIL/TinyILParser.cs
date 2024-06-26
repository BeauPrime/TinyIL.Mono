#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;

namespace TinyIL {
    static public class TinyILParser {
        #region Types

        /// <summary>
        /// Exception for an invalid IL code.
        /// </summary>
        public class InvalidILException : Exception {
            public InvalidILException(string message) : base(message) { }
            public InvalidILException(string format, params object[] args) : base(string.Format(format, args)) { }
            public InvalidILException(Exception inner, string format, params object[] args) : base(string.Format(format, args), inner) { }
        }

        internal struct LabelDefinition {
            public string Label;
            public int Index;
        }

        internal struct LateBranchResolver {
            public string Label;
            public OpCode Op;
            public Instruction Placeholder;
        }

        internal struct ConstantDefinition {
            public string PrefixedName;
            public string Value;
        }

        /// <summary>
        /// Method generation context.
        /// </summary>
        public struct MethodContext {
            public readonly MethodDefinition Definition;
            public readonly MethodBody Body;
            public readonly ILProcessor Processor;

            internal readonly List<string> VarNames;
            internal readonly List<LabelDefinition> Labels;
            internal readonly List<LateBranchResolver> Branches;
            internal readonly List<ModuleDefinition> Modules;
            internal readonly List<ConstantDefinition> Constants;

            public MethodContext(MethodDefinition definition) {
                Definition = definition;
                Body = definition.Body;
                Processor = Body.GetILProcessor();
                VarNames = new List<string>(8);
                Labels = new List<LabelDefinition>(8);
                Branches = new List<LateBranchResolver>(8);
                Constants = new List<ConstantDefinition>(4);
                Modules = new List<ModuleDefinition>(8);
                Modules.Add(Definition.Module);
            }
        }

        #endregion // Types

        #region Parsing

        /// <summary>
        /// Replaces the contents of the given method definition with parsed IL.
        /// </summary>
        static public void ReplaceWithIL(MethodDefinition methodDefinition, string ilBlock) {
            Console.WriteLine("[TinyIL] Preparing to overwrite method '{0}'...", methodDefinition.FullName);
            MethodContext context = new MethodContext(methodDefinition);
            PrepareOverwrite(context);
            ProcessLines(context, ilBlock);
            PostProcess(context);
            Console.WriteLine("[TinyIL] ...Overwrote method!", methodDefinition.FullName);
        }

        /// <summary>
        /// Parses a block of IL and generates instructions.
        /// </summary>
        static public void ProcessLines(MethodContext context, string ilBlock) {
            string[] lines = ilBlock.Split(';', '\n', '\r');
            foreach(var line in lines) {
                ProcessLine(context, line);
            }
        }

        /// <summary>
        /// Parses a line of IL and generates instructions.
        /// </summary>
        static public void ProcessLine(MethodContext context, string ilLine) {
            ilLine = ilLine.Trim();

            int commentStartIdx = ilLine.IndexOf("//");
            if (commentStartIdx >= 0) {
                ilLine = ilLine.Substring(0, commentStartIdx).TrimEnd();
            }

            if (string.IsNullOrEmpty(ilLine)) {
                return;
            }

            string op, operand;
            int spaceIdx = ilLine.IndexOf(' ');
            if (spaceIdx > 0) {
                op = ilLine.Substring(0, spaceIdx).TrimEnd();
                operand = ilLine.Substring(spaceIdx + 1).TrimStart();
            } else {
                op = ilLine;
                operand = null;
            }

            // if we have an operand, 
            if (operand != null && operand.Length > 1 && operand[0] == '#' && (op.Length == 0 || op[0] != '#')) {
                // replace with constant
                operand = FindConstant(context, operand);
            }

            if (operand == null && op.EndsWith(":")) {
                DefineLabel(context, op.Substring(0, op.Length - 1));
            } else if (op == "#var") {
                if (string.IsNullOrEmpty(operand)) {
                    throw new InvalidILException("#var must be in format [name] [type]");
                }

                int space = operand.IndexOf(' ');
                if (space < 0) {
                    throw new InvalidILException("#var must be in format [name] [type]");
                }

                string name = operand.Substring(0, space).Trim();
                string type = operand.Substring(space + 1).Trim();

                TypeReference typeRef = FindType(context, type);
                DefineVariable(context, name, typeRef);
            } else if (op == "#asmref") {
                if (string.IsNullOrEmpty(operand)) {
                    throw new InvalidILException("#asmref must have an assembly name");
                }
                ImportAssembly(context, operand);
            } else if (op == "#const") {
                if (string.IsNullOrEmpty(operand)) {
                    throw new InvalidILException("#const must be in format [name] [value]");
                }

                int space = operand.IndexOf(' ');
                if (space < 0) {
                    throw new InvalidILException("#const must be in format [name] [value]");
                }

                string name = "#" + operand.Substring(0, space).Trim();
                string value = operand.Substring(space + 1).Trim();
                DefineConstant(context, name, value);
            } else if (NoOperandCommands.TryGetValue(op, out OpCode opcode)) {
                if (!string.IsNullOrEmpty(operand)) {
                    throw new InvalidILException("opcode '{0}' does not support operands", op);
                }
                context.Processor.Emit(opcode);
            } else if (BranchCommands.TryGetValue(op, out opcode)) {
                if (string.IsNullOrEmpty(operand)) {
                    throw new InvalidILException("label targets must not be null or empty");
                }

                Instruction nop = Instruction.Create(OpCodes.Nop);
                context.Processor.Append(nop);

                context.Branches.Add(new LateBranchResolver() {
                    Label = operand,
                    Op = opcode,
                    Placeholder = nop
                });
            } else if (OperandCommands.TryGetValue(op, out InstructionGenerator generator)) {
                try {
                    Instruction inst = generator(operand, context);
                    context.Processor.Append(inst);
                } catch (Exception e) {
                    throw new InvalidILException(e, "unable to parse '{0}'", ilLine);
                }
            } else {
                throw new InvalidILException("Unrecognized or unsupported IL operation '{0}'", ilLine);
            }
        }

        #endregion // Parsing

        #region Cleanup

        /// <summary>
        /// Prepares this method to be overwritten.
        /// </summary>
        static public void PrepareOverwrite(MethodContext context) {
            context.Body.Instructions.Clear();
            context.Body.Variables.Clear();

            context.VarNames.Clear();
            context.Labels.Clear();
            context.Branches.Clear();
        }

        /// <summary>
        /// Resolves any branching instructions to point to their corresponding labels.
        /// </summary>
        static public void ResolveBranching(MethodContext context) {
            for(int i = 0; i < context.Branches.Count; i++) {
                var branch = context.Branches[i];
                if (branch.Op == OpCodes.Switch) {
                    string[] labels = branch.Label.Split(',');
                    Instruction[] targets = new Instruction[labels.Length];
                    for(int j = 0; j < labels.Length; j++) {
                        targets[j] = FindLabel(context, labels[j].Trim());
                    }
                    context.Processor.Replace(branch.Placeholder, Instruction.Create(branch.Op, targets));
                } else {
                    Instruction target = FindLabel(context, branch.Label);
                    context.Processor.Replace(branch.Placeholder, Instruction.Create(branch.Op, target));
                }
            }

            context.Branches.Clear();
        }

        /// <summary>
        /// Ensures a valid return or throw is the last instruction.
        /// </summary>
        static public void ValidateReturn(MethodContext context) {
            if (context.Body.Instructions.Count == 0) {
                context.Processor.Emit(OpCodes.Nop);
                context.Processor.Emit(OpCodes.Ret);
            } else {
                var lastInstrCode = context.Body.Instructions[context.Body.Instructions.Count - 1].OpCode.FlowControl;
                switch (lastInstrCode) {
                    case FlowControl.Return:
                    case FlowControl.Throw:
                        break;

                    default:
                        context.Processor.Emit(OpCodes.Ret);
                        break;
                }
            }
        }

        /// <summary>
        /// Performs branch resolution and return validation.
        /// </summary>
        static public void PostProcess(MethodContext context) {
            ResolveBranching(context);
            ValidateReturn(context);
            context.Definition.DebugInformation.Scope = new ScopeDebugInformation(context.Body.Instructions[0], context.Body.Instructions[context.Body.Instructions.Count - 1]);
        }

        #endregion // Cleanup

        #region Definitions

        static private void DefineVariable(MethodContext context, string varName, TypeReference varType) {
            if (string.IsNullOrEmpty(varName)) {
                throw new ArgumentNullException("varName");
            }
            if (varType == null) {
                throw new ArgumentNullException("varType");
            }

            int varIndex = context.VarNames.IndexOf(varName);
            if (varIndex >= 0) {
                throw new InvalidILException("Local variable with name '{0}' already defined for method '{1}'", varName, context.Definition.FullName);
            }

            context.Body.Variables.Add(new VariableDefinition(varType));
            context.VarNames.Add(varName);
        }

        static private void DefineLabel(MethodContext context, string labelName) {
            if (string.IsNullOrEmpty(labelName)) {
                throw new ArgumentNullException("labelName");
            }

            for (int i = 0; i < context.Labels.Count; i++) {
                if (context.Labels[i].Label.Equals(labelName, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidILException("Label with name '{0}' already defined with type '{1}' in method '{2}'", labelName, context.Body.Variables[i].VariableType.FullName, context.Definition.FullName);
                }
            }

            context.Labels.Add(new LabelDefinition() {
                Label = labelName,
                Index = context.Body.Instructions.Count
            });
        }

        static private void DefineConstant(MethodContext context, string constantName, string constantValue) {
            if (string.IsNullOrEmpty(constantName)) {
                throw new ArgumentNullException("constantName");
            }

            for(int i = 0; i < context.Constants.Count; i++) {
                if (context.Constants[i].PrefixedName.Equals(constantName, StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidILException("Constant with name '{0}' already defined with value '{1}' in method '{2}'", constantName, context.Constants[i].Value, context.Definition.FullName);
                }
            }

            context.Constants.Add(new ConstantDefinition() {
                PrefixedName = constantName,
                Value = constantValue
            });
        }

        #endregion // Definitions

        #region Importing

        static private void ImportAssembly(MethodContext context, string assemblyName) {
            if (string.IsNullOrEmpty(assemblyName)) {
                throw new ArgumentNullException("assemblyName");
            }

            ModuleDefinition rootModule = context.Definition.Module;
            AssemblyDefinition def = rootModule.AssemblyResolver.Resolve(new AssemblyNameReference(assemblyName, null));
            if (def != null) {
                foreach(var module in def.Modules) {
                    if (!context.Modules.Contains(module)) {
                        context.Modules.Add(def.MainModule);
                        // Debug.LogFormat("[TinyIL] Imported module '{0}'", module.FileName);
                        rootModule.ModuleReferences.Add(module);
                    }
                }
            } else {
                throw new InvalidILException("Could not resolve assembly '{0}'", assemblyName);
            }
        }

        #endregion // Importing

        #region Lookups

        #region Type Resolve

        static private TypeReference FindType(MethodContext context, string typeName) {
            if (string.IsNullOrEmpty(typeName)) {
                throw new InvalidILException("Empty type name");
            }

            int pointerDepth = 0;
            bool pinned = false;

            int pinnedIdx = typeName.LastIndexOf(" pinned");
            if (pinnedIdx >= 0) {
                pinned = true;
                typeName = typeName.Substring(0, pinnedIdx).TrimEnd();
            }

            int end = typeName.Length - 1;
            while(end >= 0 && typeName[end] == '*') {
                pointerDepth++;
                end--;
            }
            if (pointerDepth > 0) {
                typeName = typeName.Substring(0, end + 1);
            }

            TypeReference typeRef = FindTypeInPrimitives(context.Definition.Module, typeName.ToLower());

            if (typeRef == null && typeName.StartsWith("!!")) {
                // generic
                typeName = typeName.Substring(2);
                typeRef = FindTypeInGenericsWithTraversal(context.Definition, typeName);
            }

            if (typeRef == null && typeName.StartsWith("[") && typeName.EndsWith("]")) {
                typeName = typeName.Substring(1, typeName.Length - 2).Trim();
                typeRef = FindTypeByMacro(context, typeName);
            }

            if (typeRef == null) {
                typeRef = FindTypeInModules(context, typeName);
            }

            if (typeRef == null) {
                throw new InvalidILException("Unable to locate type with name '{0}'", typeName);
            }

            while(pointerDepth-- > 0) {
                typeRef = new Mono.Cecil.PointerType(typeRef);
            }

            if (pinned) {
                typeRef = new PinnedType(typeRef);
            }

            return context.Definition.Module.ImportReference(typeRef);
        }

        static private TypeReference FindTypeInPrimitives(ModuleDefinition module, string typeName) {
            switch (typeName) {
                case "int64": {
                    return module.TypeSystem.Int64;
                }
                case "int32": {
                    return module.TypeSystem.Int32;
                }
                case "int16": {
                    return module.TypeSystem.Int16;
                }
                case "int8": {
                    return module.TypeSystem.SByte;
                }
                case "uint64": {
                    return module.TypeSystem.UInt64;
                }
                case "uint32": {
                    return module.TypeSystem.UInt32;
                }
                case "uint16": {
                    return module.TypeSystem.UInt16;
                }
                case "uint8": {
                    return module.TypeSystem.Byte;
                }
                case "bool": {
                    return module.TypeSystem.Boolean;
                }
                case "float": {
                    return module.TypeSystem.Single;
                }
                case "double": {
                    return module.TypeSystem.Double;
                }
                case "string": {
                    return module.TypeSystem.String;
                }
                case "char": {
                    return module.TypeSystem.Char;
                }
                case "object": {
                    return module.TypeSystem.Object;
                }
                case "void": {
                    return module.TypeSystem.Void;
                }
                case "intptr": {
                    return module.TypeSystem.IntPtr;
                }
                case "uintptr": {
                    return module.TypeSystem.UIntPtr;
                }
                default: {
                    return null;
                }
            }
        }

        static private TypeReference FindTypeInGenerics(IGenericParameterProvider provider, string typeName) {
            if (provider.HasGenericParameters) {
                foreach (var param in provider.GenericParameters) {
                    if (param.Name.Equals(typeName, StringComparison.Ordinal)) {
                        return param;
                    }
                }
            }

            return null;
        }

        static private TypeReference FindTypeInGenericsWithTraversal(MethodDefinition definition, string typeName) {
            TypeReference typeRef = FindTypeInGenerics(definition, typeName);
            if (typeRef == null) {
                TypeDefinition declaringType = definition.DeclaringType;
                while (declaringType != null && typeRef == null)
                {
                    typeRef = FindTypeInGenerics(definition, typeName);
                    declaringType = declaringType.DeclaringType;
                }

                if (typeRef == null) {
                    throw new InvalidILException("Unable to locate generic type with name '{0}'", typeName);
                }
            }
            return typeRef;
        }

        static private TypeReference FindTypeByMacro(MethodContext context, string typeName) {
            if (typeName.Equals("declaringType", StringComparison.OrdinalIgnoreCase)) {
                return context.Definition.DeclaringType;
            } else {
                int spaceIdx = typeName.IndexOf(' ');
                string access = typeName.Substring(0, spaceIdx).TrimEnd();
                string name = typeName.Substring(spaceIdx + 1).TrimStart();
                if (access.Equals("param", StringComparison.OrdinalIgnoreCase) || access.Equals("arg", StringComparison.OrdinalIgnoreCase)) {
                    return FindParam(context, name).ParameterType;
                } else if (access.Equals("var", StringComparison.OrdinalIgnoreCase)) {
                    return FindVariable(context, name).VariableType;
                }
            }
            
            throw new InvalidILException("Unrecognized type macro '{0}'", typeName);
        }

        static private TypeReference FindTypeInModules(MethodContext context, string typeName) {
            TypeReference typeRef;
            foreach (var module in context.Modules) {
                if (module.TryGetTypeReference(typeName, out typeRef)) {
                    return typeRef;
                }
            }
            return null;
        }

        #endregion // Type Resolve

        static private ParameterDefinition FindParam(MethodContext context, string paramName) {
            if (string.IsNullOrEmpty(paramName)) {
                throw new InvalidILException("Empty parameter name");
            }

            foreach (var param in context.Definition.Parameters) {
                if (param.Name.Equals(paramName, StringComparison.Ordinal)) {
                    return param;
                }
            }

            throw new InvalidILException("No parameter with name '{0}' found in method '{1}'", paramName, context.Definition.FullName);
        }

        static private VariableDefinition FindVariable(MethodContext context, string varName) {
            if (string.IsNullOrEmpty(varName)) {
                throw new InvalidILException("Empty variable name");
            }

            int varIndex;
            if (int.TryParse(varName, out varIndex)) {
                if (varIndex < 0 || varIndex >= context.Body.Variables.Count) {
                    throw new InvalidILException("No local variable with index {0} found in method '{1}'", varIndex, context.Definition.FullName);
                }
            } else {
                varIndex = context.VarNames.IndexOf(varName);
                if (varIndex < 0) {
                    throw new InvalidILException("No local variable with name '{0}' found in method '{1}'", varName, context.Definition.FullName);
                }
            }
            return context.Body.Variables[varIndex];
        }

        static private Instruction FindLabel(MethodContext context, string labelName) {
            if (string.IsNullOrEmpty(labelName)) {
                throw new InvalidILException("Empty label name");
            }

            for(int i = 0; i < context.Labels.Count; i++) {
                if (context.Labels[i].Label.Equals(labelName, StringComparison.OrdinalIgnoreCase)) {
                    return context.Body.Instructions[context.Labels[i].Index];
                }
            }

            throw new InvalidILException("No label with name '{0}' found in method '{1}'", labelName, context.Definition.FullName);
        }

        static private MethodReference FindMethod(MethodContext context, string methodName) {
            int scopeIdx = methodName.IndexOf("::");
            if (scopeIdx < 0) {
                throw new InvalidILException("Malformed method name '{0}'", methodName);
            }

            string typeName = methodName.Substring(0, scopeIdx);
            string scopedMethodName = methodName.Substring(scopeIdx + 2);

            // parse argument types
            int argOpenIdx = scopedMethodName.IndexOf('(');
            int argCloseIdx = scopedMethodName.LastIndexOf(')');

            if (argOpenIdx < 0 || argCloseIdx < 0) {
                throw new InvalidILException("Malformed method name '{0}'", methodName);
            }

            string paramList = scopedMethodName.Substring(argOpenIdx + 1, argCloseIdx - argOpenIdx - 1);
            TypeReference[] parameterTypes = ParseTypeList(context, paramList);
            scopedMethodName = scopedMethodName.Substring(0, argOpenIdx).Trim(); 

            TypeReference type = FindType(context, typeName);
            TypeDefinition typeDef = type.Resolve();
            if (typeDef == null) {
                throw new InvalidILException("No type name found with the name '{0}'", typeName);
            }

            TypeDefinition current = typeDef;
            while (current != null) {
                if (current.HasMethods) {
                    foreach (var method in current.Methods) {
                        if (method.Name != scopedMethodName) {
                            continue;
                        }
                        if (method.Parameters.Count != parameterTypes.Length) {
                            continue;
                        }
                        bool parametersMatch = true;
                        for(int i = 0; i < parameterTypes.Length; i++) {
                            if (method.Parameters[i].ParameterType.FullName != parameterTypes[i].FullName) {
                                parametersMatch = false;
                                break;
                            }
                        }
                        if (parametersMatch) {
                            context.Definition.Module.ImportReference(method.ReturnType);
                            return context.Definition.Module.ImportReference(method);
                        }
                    }
                }
                current = current.BaseType?.Resolve();
            }

            throw new InvalidILException("No method with name '{0}' found on type '{1}'", scopedMethodName, typeDef.FullName);
        }

        static private MemberReference FindToken(MethodContext context, string methodName) {
            // TODO: Implement
            throw new NotImplementedException();
        }

        static private FieldReference FindField(MethodContext context, string fieldName, bool isStatic) {
            int scopeIdx = fieldName.IndexOf("::");
            if (scopeIdx < 0) {
                throw new InvalidILException("Malformed field name '{0}'", fieldName);
            }

            string typeName = fieldName.Substring(0, scopeIdx);
            string scopedFieldName = fieldName.Substring(scopeIdx + 2);

            TypeReference type = FindType(context, typeName);
            TypeDefinition typeDef = type.Resolve();
            if (typeDef == null) {
                throw new InvalidILException("No type name found with the name '{0}'", typeName);
            }

            TypeDefinition current = typeDef;
            while (current != null) {
                if (current.HasFields) {
                    foreach (var field in current.Fields) {
                        if (field.Name == scopedFieldName) {
                            context.Definition.Module.ImportReference(field.FieldType);
                            return context.Definition.Module.ImportReference(field);
                        }
                    }
                }
                current = current.BaseType?.Resolve();
            }

            throw new InvalidILException("No {0} field with name '{1}' found on type '{2}'", isStatic ? "static" : "instance", scopedFieldName, typeDef.FullName) ;
        }

        static private string FindConstant(MethodContext context, string constantName) {
            if (string.IsNullOrEmpty(constantName)) {
                throw new InvalidILException("Empty constant name");
            }

            for (int i = 0; i < context.Constants.Count; i++) {
                if (context.Constants[i].PrefixedName.Equals(constantName, StringComparison.OrdinalIgnoreCase)) {
                    return context.Constants[i].Value;
                }
            }

            throw new InvalidILException("No constant with name '{0}' found in method '{1}'", constantName, context.Definition.FullName);
        }

        static private CallSite ParseCallSite(MethodContext context, string descriptor) {
            string convStr, sigStr;
            int pipeChar = descriptor.IndexOf('|');
            if (pipeChar >= 0) {
                convStr = descriptor.Substring(0, pipeChar).TrimEnd();
                sigStr = descriptor.Substring(pipeChar + 1).TrimStart();
            } else {
                convStr = null;
                sigStr = descriptor;
            }

            MethodCallingConvention convention = MethodCallingConvention.Default;
            if (!string.IsNullOrEmpty(convStr)) {
                if (convStr.Equals("cdecl", StringComparison.OrdinalIgnoreCase)) {
                    convention = MethodCallingConvention.C;
                } else if (!Enum.TryParse(convStr, true, out convention)) {
                    throw new InvalidILException("Unable to parse '{0}' into valid calling convention", convStr);
                }
            }

            int argOpenIdx = sigStr.IndexOf('(');
            int argCloseIdx = sigStr.LastIndexOf(')');

            if (argOpenIdx < 0 || argCloseIdx < 0) {
                throw new InvalidILException("Malformed method signature '{0}'", sigStr);
            }

            string paramList = sigStr.Substring(argOpenIdx + 1, argCloseIdx - argOpenIdx - 1);
            TypeReference[] parameterTypes = ParseTypeList(context, paramList);

            string returnTypeStr = sigStr.Substring(0, argOpenIdx).TrimEnd();
            if (string.IsNullOrEmpty(returnTypeStr)) {
                throw new InvalidILException("Malformed method signature '{0}'", sigStr);
            }

            TypeReference returnType = FindType(context, returnTypeStr);
            CallSite site = new CallSite(returnType) { CallingConvention = convention };
            if (convention == MethodCallingConvention.ThisCall) {
                site.HasThis = true;
            }
            foreach(var paramType in parameterTypes) {
                site.Parameters.Add(new ParameterDefinition(paramType));
            }
            return site;
        }

        static private string ParseUserString(MethodContext context, string stringVar) {
            if (stringVar.StartsWith("\"") && stringVar.EndsWith("\"")) {
                stringVar = stringVar.Substring(1, stringVar.Length - 2);
            }
            return stringVar;
        }

        static private TypeReference[] ParseTypeList(MethodContext context, string typeList) {
            // TODO: Account for generics
            if (string.IsNullOrEmpty(typeList)) {
                return Array.Empty<TypeReference>();
            }
            string[] split = typeList.Split(',');
            TypeReference[] typeRefs = new TypeReference[split.Length];
            for(int i = 0; i < split.Length; i++) {
                string trimmed = split[i].Trim();
                typeRefs[i] = FindType(context, trimmed); 
            }
            return typeRefs;
        }

        #endregion // Lookups

        #region Handlers

        private delegate Instruction InstructionGenerator(string argument, MethodContext context);

        static private readonly Dictionary<string, OpCode> NoOperandCommands = new Dictionary<string, OpCode>(StringComparer.OrdinalIgnoreCase) {
            { "add", OpCodes.Add },
            { "add.ovf", OpCodes.Add_Ovf },
            { "add.ovf.un", OpCodes.Add_Ovf_Un },
            { "and", OpCodes.And },
            { "arglist", OpCodes.Arglist },
            { "break", OpCodes.Break },
            { "ceq", OpCodes.Ceq },
            { "cgt", OpCodes.Cgt },
            { "cgt.un", OpCodes.Cgt_Un },
            { "ckfinite", OpCodes.Ckfinite },
            { "clt", OpCodes.Clt },
            { "clt.un", OpCodes.Clt_Un },
            { "conv.i", OpCodes.Conv_I },
            { "conv.i1", OpCodes.Conv_I1 },
            { "conv.i2", OpCodes.Conv_I2 },
            { "conv.i4", OpCodes.Conv_I4 },
            { "conv.i8", OpCodes.Conv_I8 },
            { "conv.ovf.i", OpCodes.Conv_Ovf_I },
            { "conv.ovf.i.un", OpCodes.Conv_Ovf_I_Un },
            { "conv.ovf.i1", OpCodes.Conv_Ovf_I1 },
            { "conv.ovf.i1.un", OpCodes.Conv_Ovf_I1_Un },
            { "conv.ovf.i2", OpCodes.Conv_Ovf_I2 },
            { "conv.ovf.i2.un", OpCodes.Conv_Ovf_I2_Un },
            { "conv.ovf.i4", OpCodes.Conv_Ovf_I4 },
            { "conv.ovf.i4.un", OpCodes.Conv_Ovf_I4_Un },
            { "conv.ovf.i8", OpCodes.Conv_Ovf_I8 },
            { "conv.ovf.i8.un", OpCodes.Conv_Ovf_I8_Un },
            { "conv.ovf.u", OpCodes.Conv_Ovf_U },
            { "conv.ovf.u.un", OpCodes.Conv_Ovf_U_Un },
            { "conv.ovf.u1", OpCodes.Conv_Ovf_U1 },
            { "conv.ovf.u1.un", OpCodes.Conv_Ovf_U1_Un },
            { "conv.ovf.u2", OpCodes.Conv_Ovf_U2 },
            { "conv.ovf.u2.un", OpCodes.Conv_Ovf_U2_Un },
            { "conv.ovf.u4", OpCodes.Conv_Ovf_U4 },
            { "conv.ovf.u4.un", OpCodes.Conv_Ovf_U4_Un },
            { "conv.ovf.u8", OpCodes.Conv_Ovf_U8 },
            { "conv.ovf.u8.un", OpCodes.Conv_Ovf_U8_Un },
            { "conv.r.un", OpCodes.Conv_R_Un },
            { "conv.r4", OpCodes.Conv_R4 },
            { "conv.r8", OpCodes.Conv_R8 },
            { "conv.u", OpCodes.Conv_U },
            { "conv.u1", OpCodes.Conv_U1 },
            { "conv.u2", OpCodes.Conv_U2 },
            { "conv.u4", OpCodes.Conv_U4 },
            { "conv.u8", OpCodes.Conv_U8 },
            { "cpblk", OpCodes.Cpblk },
            { "div", OpCodes.Div },
            { "div.un", OpCodes.Div_Un },
            { "dup", OpCodes.Dup },
            { "endfilter", OpCodes.Endfilter },
            { "endfinally", OpCodes.Endfinally },
            { "initblk", OpCodes.Initblk },
            { "ldarg.0", OpCodes.Ldarg_0 },
            { "ldarg.1", OpCodes.Ldarg_1 },
            { "ldarg.2", OpCodes.Ldarg_2 },
            { "ldarg.3", OpCodes.Ldarg_3 },
            { "ldc.i4.0", OpCodes.Ldc_I4_0 },
            { "ldc.i4.1", OpCodes.Ldc_I4_1 },
            { "ldc.i4.2", OpCodes.Ldc_I4_2 },
            { "ldc.i4.3", OpCodes.Ldc_I4_3 },
            { "ldc.i4.4", OpCodes.Ldc_I4_4 },
            { "ldc.i4.5", OpCodes.Ldc_I4_5 },
            { "ldc.i4.6", OpCodes.Ldc_I4_6 },
            { "ldc.i4.7", OpCodes.Ldc_I4_7 },
            { "ldc.i4.8", OpCodes.Ldc_I4_8 },
            { "ldc.i4.m1", OpCodes.Ldc_I4_M1 },
            { "ldelem.i", OpCodes.Ldelem_I },
            { "ldelem.i1", OpCodes.Ldelem_I1 },
            { "ldelem.i2", OpCodes.Ldelem_I2 },
            { "ldelem.i4", OpCodes.Ldelem_I4 },
            { "ldelem.i8", OpCodes.Ldelem_I8 },
            { "ldelem.r4", OpCodes.Ldelem_R4 },
            { "ldelem.r8", OpCodes.Ldelem_R8 },
            { "ldelem.ref", OpCodes.Ldelem_Ref },
            { "ldelem.u1", OpCodes.Ldelem_U1 },
            { "ldelem.u2", OpCodes.Ldelem_U2 },
            { "ldelem.u4", OpCodes.Ldelem_U4 },
            { "ldind.i", OpCodes.Ldind_I },
            { "ldind.i1", OpCodes.Ldind_I1 },
            { "ldind.i2", OpCodes.Ldind_I2 },
            { "ldind.i4", OpCodes.Ldind_I4 },
            { "ldind.i8", OpCodes.Ldind_I8 },
            { "ldind.r4", OpCodes.Ldind_R4 },
            { "ldind.r8", OpCodes.Ldind_R8 },
            { "ldind.ref", OpCodes.Ldind_Ref },
            { "ldind.u1", OpCodes.Ldind_U1 },
            { "ldind.u2", OpCodes.Ldind_U2 },
            { "ldind.u4", OpCodes.Ldind_U4 },
            { "ldlen", OpCodes.Ldlen },
            { "ldloc.0", OpCodes.Ldloc_0 },
            { "ldloc.1", OpCodes.Ldloc_1 },
            { "ldloc.2", OpCodes.Ldloc_2 },
            { "ldloc.3", OpCodes.Ldloc_3 },
            { "ldnull", OpCodes.Ldnull },
            { "localloc", OpCodes.Localloc },
            { "mul", OpCodes.Mul },
            { "mul.ovf", OpCodes.Mul_Ovf },
            { "mul.ovf.un", OpCodes.Mul_Ovf_Un },
            { "neg", OpCodes.Neg },
            { "nop", OpCodes.Nop },
            { "not", OpCodes.Not },
            { "or", OpCodes.Or },
            { "pop", OpCodes.Pop },
            { "readonly.", OpCodes.Readonly },
            { "refanytype", OpCodes.Refanytype },
            { "rem", OpCodes.Rem },
            { "rem.un", OpCodes.Rem_Un },
            { "ret", OpCodes.Ret },
            { "rethrow", OpCodes.Rethrow },
            { "shl", OpCodes.Shl },
            { "shr", OpCodes.Shr },
            { "shr.un", OpCodes.Shr_Un },
            { "stelem.i", OpCodes.Stelem_I },
            { "stelem.i1", OpCodes.Stelem_I1 },
            { "stelem.i2", OpCodes.Stelem_I2 },
            { "stelem.i4", OpCodes.Stelem_I4 },
            { "stelem.i8", OpCodes.Stelem_I8 },
            { "stelem.r4", OpCodes.Stelem_R4 },
            { "stelem.r8", OpCodes.Stelem_R8 },
            { "stelem.ref", OpCodes.Stelem_Ref },
            { "stind.i", OpCodes.Stind_I },
            { "stind.i1", OpCodes.Stind_I1 },
            { "stind.i2", OpCodes.Stind_I2 },
            { "stind.i4", OpCodes.Stind_I4 },
            { "stind.i8", OpCodes.Stind_I8 },
            { "stind.r4", OpCodes.Stind_R4 },
            { "stind.r8", OpCodes.Stind_R8 },
            { "stind.ref", OpCodes.Stind_Ref },
            { "stloc.0", OpCodes.Stloc_0 },
            { "stloc.1", OpCodes.Stloc_1 },
            { "stloc.2", OpCodes.Stloc_2 },
            { "stloc.3", OpCodes.Stloc_3 },
            { "sub", OpCodes.Sub },
            { "sub.ovf", OpCodes.Sub_Ovf },
            { "sub.ovf.un", OpCodes.Sub_Ovf_Un },
            { "tail.", OpCodes.Tail },
            { "throw", OpCodes.Throw },
            { "volatile.", OpCodes.Volatile },
            { "xor", OpCodes.Xor },
        };

        static private readonly Dictionary<string, InstructionGenerator> OperandCommands = new Dictionary<string, InstructionGenerator>(StringComparer.OrdinalIgnoreCase) {
            { "box", (a, d) => { return Instruction.Create(OpCodes.Box, FindType(d, a)); } },
            { "call", (a, d) => { return Instruction.Create(OpCodes.Call, FindMethod(d, a)); } },
            { "calli", (a, d) => { return Instruction.Create(OpCodes.Calli, ParseCallSite(d, a)); } },
            { "callvirt", (a, d) => { return Instruction.Create(OpCodes.Callvirt, FindMethod(d, a)); } },
            { "castclass", (a, d) => { return Instruction.Create(OpCodes.Castclass, FindType(d, a)); } },
            { "constrained.", (a, d) => { return Instruction.Create(OpCodes.Constrained, FindType(d, a)); } },
            { "cpobj", (a, d) => { return Instruction.Create(OpCodes.Cpobj, FindType(d, a)); } },
            { "initobj", (a, d) => { return Instruction.Create(OpCodes.Initobj, FindType(d, a)); } },
            { "isinst", (a, d) => { return Instruction.Create(OpCodes.Isinst, FindType(d, a)); } },
            { "jmp", (a, d) => { return Instruction.Create(OpCodes.Jmp, FindMethod(d, a)); } },
            { "ldarg", (a, d) => { return Instruction.Create(OpCodes.Ldarg, FindParam(d, a)); } },
            { "ldarg.s", (a, d) => { return Instruction.Create(OpCodes.Ldarg_S, FindParam(d, a)); } },
            { "ldarga", (a, d) => { return Instruction.Create(OpCodes.Ldarga, FindParam(d, a)); } },
            { "ldarga.s", (a, d) => { return Instruction.Create(OpCodes.Ldarga_S, FindParam(d, a)); } },
            { "ldc.i4", (a, d) => { return Instruction.Create(OpCodes.Ldc_I4, ParseInt(a)); } },
            { "ldc.i4.s", (a, d) => { return Instruction.Create(OpCodes.Ldc_I4_S, ParseSByte(a)); } },
            { "ldc.i8", (a, d) => { return Instruction.Create(OpCodes.Ldc_I8, ParseLong(a)); } },
            { "ldc.r4", (a, d) => { return Instruction.Create(OpCodes.Ldc_R4, float.Parse(a)); } },
            { "ldc.r8", (a, d) => { return Instruction.Create(OpCodes.Ldc_R8, double.Parse(a)); } },
            
            // CUSTOM
            { "ldc.u4", (a, d) => { return Instruction.Create(OpCodes.Ldc_I4, Cast<uint, int>(ParseUint(a))); } },
            { "ldc.u8", (a, d) => { return Instruction.Create(OpCodes.Ldc_I8, Cast<ulong, long>(ParseUlong(a))); } },
            
            { "ldelem", (a, d) => { return Instruction.Create(OpCodes.Ldelem_Any, FindType(d, a)); } },
            { "ldelema", (a, d) => { return Instruction.Create(OpCodes.Ldelema, FindType(d, a)); } },
            { "ldfld", (a, d) => { return Instruction.Create(OpCodes.Ldfld, FindField(d, a, false)); } },
            { "ldflda", (a, d) => { return Instruction.Create(OpCodes.Ldflda, FindField(d, a, false)); } },
            { "ldftn", (a, d) => { return Instruction.Create(OpCodes.Ldftn, FindMethod(d, a)); } },
            { "ldloc", (a, d) => { return Instruction.Create(OpCodes.Ldloc, FindVariable(d, a)); } },
            { "ldloc.s", (a, d) => { return Instruction.Create(OpCodes.Ldloc_S, FindVariable(d, a)); } },
            { "ldloca", (a, d) => { return Instruction.Create(OpCodes.Ldloca, FindVariable(d, a)); } },
            { "ldloca.s", (a, d) => { return Instruction.Create(OpCodes.Ldloca_S, FindVariable(d, a)); } },
            { "ldobj", (a, d) => { return Instruction.Create(OpCodes.Ldobj, FindType(d, a)); } },
            { "ldsflda", (a, d) => { return Instruction.Create(OpCodes.Ldsfld, FindField(d, a, true)); } },
            { "ldsfld", (a, d) => { return Instruction.Create(OpCodes.Ldsfld, FindField(d, a, true)); } },
            { "ldstr", (a, d) => { return Instruction.Create(OpCodes.Ldstr, ParseUserString(d, a)); } },

            //{ "ldtoken", (a, d) => { return Instruction.Create(OpCodes.Ldtoken, FindToken(d, a)); } },

            { "ldvirtfn", (a, d) => { return Instruction.Create(OpCodes.Ldvirtftn, FindMethod(d, a)); } },
            { "mkrefany", (a, d) => { return Instruction.Create(OpCodes.Mkrefany, FindType(d, a)); } },
            { "newarr", (a, d) => { return Instruction.Create(OpCodes.Newarr, FindType(d, a)); } },
            { "newobj", (a, d) => { return Instruction.Create(OpCodes.Newobj, FindMethod(d, a)); } },
            { "refanyval", (a, d) => { return Instruction.Create(OpCodes.Refanyval, FindType(d, a)); } },
            { "sizeof", (a, d) => { return Instruction.Create(OpCodes.Sizeof, FindType(d, a)); } },
            { "starg", (a, d) => { return Instruction.Create(OpCodes.Starg, FindParam(d, a)); } },
            { "starg.s", (a, d) => { return Instruction.Create(OpCodes.Starg_S, FindParam(d, a)); } },
            { "stelem", (a, d) => { return Instruction.Create(OpCodes.Stelem_Any, FindType(d, a)); } },
            { "stfld", (a, d) => { return Instruction.Create(OpCodes.Stfld, FindField(d, a, false)); } },
            { "stloc", (a, d) => { return Instruction.Create(OpCodes.Stloc, FindVariable(d, a)); } },
            { "stloc.s", (a, d) => { return Instruction.Create(OpCodes.Stloc_S, FindVariable(d, a)); } },
            { "stobj", (a, d) => { return Instruction.Create(OpCodes.Stobj, FindType(d, a)); } },
            { "stsfld", (a, d) => { return Instruction.Create(OpCodes.Stsfld, FindField(d, a, true)); } },
            { "unaligned.", (a, d) => { return Instruction.Create(OpCodes.Unaligned, byte.Parse(a)); } },
            { "unaligned.1", (a, d) => { return Instruction.Create(OpCodes.Unaligned, (byte) 1); } },
            { "unaligned.2", (a, d) => { return Instruction.Create(OpCodes.Unaligned, (byte) 2); } },
            { "unaligned.4", (a, d) => { return Instruction.Create(OpCodes.Unaligned, (byte) 4); } },
            { "unbox", (a, d) => { return Instruction.Create(OpCodes.Unbox, FindType(d, a)); } },
            { "unbox.any", (a, d) => { return Instruction.Create(OpCodes.Unbox_Any, FindType(d, a)); } },
        };

        static private readonly Dictionary<string, OpCode> BranchCommands = new Dictionary<string, OpCode>(StringComparer.OrdinalIgnoreCase) {
            { "beq", OpCodes.Beq },
            { "beq.s", OpCodes.Beq_S },
            { "bge", OpCodes.Bge },
            { "bge.s", OpCodes.Bge_S },
            { "bge.un", OpCodes.Bge_Un },
            { "bge.un.s", OpCodes.Bge_Un_S },
            { "bgt", OpCodes.Bgt },
            { "bgt.s", OpCodes.Bgt_S },
            { "bgt.un", OpCodes.Bgt_Un },
            { "bgt.un.s", OpCodes.Bgt_Un_S },
            { "ble", OpCodes.Ble },
            { "ble.s", OpCodes.Ble_S },
            { "ble.un", OpCodes.Ble_Un },
            { "ble.un.s", OpCodes.Ble_Un_S },
            { "blt", OpCodes.Blt },
            { "blt.s", OpCodes.Blt_S },
            { "blt.un", OpCodes.Blt_Un },
            { "blt.un.s", OpCodes.Blt_Un_S },
            { "bne.un", OpCodes.Bne_Un },
            { "bne.un.s", OpCodes.Bne_Un_S },
            { "br", OpCodes.Br },
            { "br.s", OpCodes.Br_S },
            { "brfalse", OpCodes.Brfalse },
            { "brfalse.s", OpCodes.Brfalse_S },
            { "brtrue", OpCodes.Brtrue },
            { "brtrue.s", OpCodes.Brtrue_S },
            { "leave", OpCodes.Leave },
            { "leave.s", OpCodes.Leave_S },
            { "switch", OpCodes.Switch },
        };

        #endregion // Handlers

        #region Utils

        /// <summary>
        /// Opens an assembly for reading/writing.
        /// </summary>
        static public AssemblyDefinition OpenReadWrite(string assemblyPath, out bool debugSymbols) {
            string pdbPath = Path.ChangeExtension(assemblyPath, "pdb");
            if (File.Exists(pdbPath)) {
                try {
                    debugSymbols = true;
                    return AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters() { ReadWrite = true, ReadSymbols = true, SymbolReaderProvider = new PdbReaderProvider() });
                } catch (Exception e) {
                    Console.WriteLine("Error when attempting to read debug symbols:\n{0}", e.ToString());
                }
            }

            debugSymbols = false;
            return AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters() { ReadWrite = true });
        }

        /// <summary>
        /// Opens an assembly for reading/writing.
        /// </summary>
        static public AssemblyDefinition OpenReadWrite(byte[] assemblyData, byte[] pdbData, out bool debugSymbols) {
            ReaderParameters readerParams = new ReaderParameters() {
                ReadWrite = true
            };

            if (pdbData != null && pdbData.Length > 0) {
                try {
                    debugSymbols = true;
                    MemoryStream pdbStream = new MemoryStream(pdbData);
                    readerParams.ReadSymbols = true;
                    readerParams.SymbolStream = pdbStream;
                    readerParams.SymbolReaderProvider = new PortablePdbReaderProvider();
                    return AssemblyDefinition.ReadAssembly(new MemoryStream(assemblyData), readerParams);
                } catch (Exception e) {
                    Console.WriteLine("Error when attempting to read debug symbols:\n{0}", e.ToString());
                }
            }

            debugSymbols = false;
            return AssemblyDefinition.ReadAssembly(new MemoryStream(assemblyData), readerParams);
        }

        /// <summary>
        /// Adds assembly search directories.
        /// </summary>
        static public void AddAssemblySearchDirectories(AssemblyDefinition asmDef, IEnumerable<string> directories) {
            AddAssemblySearchDirectories(asmDef.MainModule.AssemblyResolver, directories);
        }

        /// <summary>
        /// Adds assembly search directories.
        /// </summary>
        static public void AddAssemblySearchDirectories(ModuleDefinition module, IEnumerable<string> directories) {
            AddAssemblySearchDirectories(module.AssemblyResolver, directories);
        }

        /// <summary>
        /// Adds assembly search directories.
        /// </summary>
        static public void AddAssemblySearchDirectories(IAssemblyResolver resolver, IEnumerable<string> directories) {
            BaseAssemblyResolver asmResolver = resolver as BaseAssemblyResolver;
            if (asmResolver == null) {
                throw new InvalidOperationException();
            }

            foreach (var dir in directories) {
                asmResolver.AddSearchDirectory(dir);
            }
        }

        /// <summary>
        /// Adds an assembly search directory.
        /// </summary>
        static public void AddAssemblySearchDirectory(AssemblyDefinition asmDef, string directory) {
            AddAssemblySearchDirectory(asmDef.MainModule.AssemblyResolver, directory);
        }

        /// <summary>
        /// Adds an assembly search directory.
        /// </summary>
        static public void AddAssemblySearchDirectory(ModuleDefinition module, string directory) {
            AddAssemblySearchDirectory(module.AssemblyResolver, directory);
        }

        /// <summary>
        /// Adds an assembly search directory..
        /// </summary>
        static public void AddAssemblySearchDirectory(IAssemblyResolver resolver, string directory) {
            BaseAssemblyResolver asmResolver = resolver as BaseAssemblyResolver;
            if (asmResolver == null) {
                throw new InvalidOperationException();
            }

            asmResolver.AddSearchDirectory(directory);
        }

        /// <summary>
        /// Finds the first custom attribute with the given type name.
        /// </summary>
        static public bool FindCustomAttribute(ICustomAttributeProvider provider, string attrName, out CustomAttribute attribute) {
            if (provider.HasCustomAttributes) {
                foreach (var attr in provider.CustomAttributes) {
                    if (attr.AttributeType.Name == attrName) {
                        attribute = attr;
                        return true;
                    }
                }
            }

            attribute = null;
            return false;
        }

        #region Parsing

        static private sbyte ParseSByte(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return sbyte.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return sbyte.Parse(str);
            }
        }

        static private byte ParseByte(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return byte.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return byte.Parse(str);
            }
        }

        static private short ParseShort(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return short.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return short.Parse(str);
            }
        }

        static private ushort ParseUShort(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return ushort.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return ushort.Parse(str);
            }
        }

        static private int ParseInt(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return int.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return int.Parse(str);
            }
        }

        static private uint ParseUint(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return uint.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return uint.Parse(str);
            }
        }

        static private long ParseLong(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return long.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return long.Parse(str);
            }
        }

        static private ulong ParseUlong(string str) {
            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                return ulong.Parse(str.Substring(2), NumberStyles.HexNumber);
            } else {
                return ulong.Parse(str);
            }
        }

        static private unsafe TOut Cast<TIn, TOut>(TIn value) where TIn : unmanaged where TOut : unmanaged {
            return *(TOut*) (&value);
        }

        #endregion // Parsing

        #endregion // Utils

        #region Traversal

        /// <summary>
        /// Traverses and modifies types in the given assembly.
        /// </summary>
        static public int TraverseTypesAndModify(AssemblyDefinition asmDef, Predicate<TypeDefinition> predicate, ProcessTypeDelegate modifyAction, ref PatchFileCache patchCache, object context) {
            if (asmDef == null) {
                throw new ArgumentNullException("asmDef");
            }
            if (modifyAction == null) {
                throw new ArgumentNullException("modifyAction");
            }

            int modifiedCount = 0;
            HashSet<TypeDefinition> processed = new HashSet<TypeDefinition>();
            Stack<TypeDefinition> types = new Stack<TypeDefinition>(asmDef.MainModule.Types);
            while (types.Count > 0) {
                var type = types.Pop();
                processed.Add(type);

                if (type.FullName == "<Module>" || (predicate != null && !predicate(type))) {
                    continue;
                }

                modifiedCount += modifyAction(type, ref patchCache, context);

                if (type.HasNestedTypes) {
                    foreach (var nested in type.NestedTypes) {
                        if (!processed.Contains(nested)) {
                            types.Push(nested);
                        }
                    }
                }
            }
            return modifiedCount;
        }

        /// <summary>
        /// Traverses and modifies methods in the given assembly.
        /// </summary>
        static public int TraverseMethodsAndModify(AssemblyDefinition asmDef, Predicate<MethodDefinition> predicate, ProcessMethodDelegate modifyAction, ref PatchFileCache patchCache, object context) {
            if (asmDef == null) {
                throw new ArgumentNullException("asmDef");
            }
            if (modifyAction == null) {
                throw new ArgumentNullException("modifyAction");
            }

            int modifiedCount = 0;
            HashSet<TypeDefinition> processed = new HashSet<TypeDefinition>();
            Stack<TypeDefinition> types = new Stack<TypeDefinition>(asmDef.MainModule.Types);
            while (types.Count > 0) {
                var type = types.Pop();
                processed.Add(type);

                if (type.FullName == "<Module>" || !type.HasMethods) {
                    continue;
                }

                foreach (var method in type.Methods) {
                    if (predicate == null || predicate(method)) {
                        if (modifyAction(method, ref patchCache, context)) {
                            modifiedCount++;
                        }
                    }
                }

                if (type.HasNestedTypes) {
                    foreach (var nested in type.NestedTypes) {
                        if (!processed.Contains(nested)) {
                            types.Push(nested);
                        }
                    }
                }
            }
            return modifiedCount;
        }

        /// <summary>
        /// Traverses and collects methods that pass a given predicate.
        /// </summary>
        static public int TraverseMethodsAndCollect(AssemblyDefinition asmDef, Predicate<MethodDefinition> predicate, ICollection<MethodDefinition> output) {
            if (asmDef == null) {
                throw new ArgumentNullException("asmDef");
            }
            if (predicate == null) {
                throw new ArgumentNullException("predicate");
            }
            if (output == null) {
                throw new ArgumentNullException("output");
            }

            int collectedCount = 0;
            HashSet<TypeDefinition> processed = new HashSet<TypeDefinition>();
            Stack<TypeDefinition> types = new Stack<TypeDefinition>(asmDef.MainModule.Types);
            while (types.Count > 0) {
                var type = types.Pop();
                processed.Add(type);

                if (type.FullName == "<Module>" || !type.HasMethods) {
                    continue;
                }

                foreach (var method in type.Methods) {
                    if (predicate == null || predicate(method)) {
                        output.Add(method);
                    }
                }

                if (type.HasNestedTypes) {
                    foreach (var nested in type.NestedTypes) {
                        if (!processed.Contains(nested)) {
                            types.Push(nested);
                        }
                    }
                }
            }
            return collectedCount;
        }

        #endregion // Traversal
    }

    /// <summary>
    /// Patch file cache utility.
    /// </summary>
    public struct PatchFileCache {

        internal string SearchDirectory;
        internal Dictionary<string, string> PatchMap;

        public PatchFileCache(string sourceDirectory) {
            SearchDirectory = string.IsNullOrEmpty(sourceDirectory) ? "./" : sourceDirectory;
            PatchMap = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public string FindPatch(string fileAndPatchName) {
            string patch;
            if (PatchMap.TryGetValue(fileAndPatchName, out patch)) {
                return patch;
            }

            string fileName, patchName;
            int colonIdx = fileAndPatchName.IndexOf(':');
            if (colonIdx <= 0) {
                throw new ArgumentException(string.Format("Patch name '{0}' is not formatted properly - expecting format FILENAME:PATCHNAME", fileAndPatchName));
            } else {
                fileName = fileAndPatchName.Substring(0, colonIdx).Trim();
                patchName = fileAndPatchName.Substring(colonIdx + 1).Trim();
            }

            if (TryReadPatchFile(SearchDirectory, fileName, PatchMap)) {
                if (PatchMap.TryGetValue(fileAndPatchName, out patch)) {
                    return patch;
                }
            }

            throw new FileNotFoundException(string.Format("No patch found for file '{0}', name '{1}' from source directory '{2}'", fileName, patchName, SearchDirectory));
        }

        static internal bool TryReadPatchFile(string directory, string fileName, Dictionary<string, string> output) {
            StringBuilder patchBuilder = new StringBuilder(2048);
            string currentPatchKey = null;
            bool modified = false;

            foreach (var filePath in Directory.GetFiles(directory, fileName + ".ilpatch", SearchOption.AllDirectories)) {
                using (FileStream stream = File.OpenRead(filePath))
                using (StreamReader reader = new StreamReader(stream)) {
                    while (!reader.EndOfStream) {
                        string line = reader.ReadLine().TrimStart();
                        if (string.IsNullOrEmpty(line)) {
                            continue;
                        }

                        if (line.StartsWith("==")) {
                            if (patchBuilder.Length > 0) {
                                if (output.ContainsKey(currentPatchKey)) {
                                    throw new InvalidOperationException(string.Format("Duplicate patches with key '{0}'", currentPatchKey));
                                }

                                Console.WriteLine("[TinyIL] Located patch '{0}'", currentPatchKey);
                                output.Add(currentPatchKey, patchBuilder.ToString());
                                patchBuilder.Length = 0;
                                modified = true;
                            }

                            currentPatchKey = string.Concat(fileName, ":", line.Substring(2).Trim());
                        } else if (line.StartsWith("//")) {
                            // skip - it's a comment
                        } else {
                            if (string.IsNullOrEmpty(currentPatchKey)) {
                                throw new InvalidOperationException("Invalid patch file format - patch contents must be preceded by '== PATCH_NAME'");
                            }
                            patchBuilder.Append(line).Append('\n');
                        }
                    }

                    if (patchBuilder.Length > 0) {
                        if (string.IsNullOrEmpty(currentPatchKey)) {
                            throw new InvalidOperationException("Invalid patch file format - patch contents must be preceded by '== PATCH_NAME'");
                        }

                        if (output.ContainsKey(currentPatchKey)) {
                            throw new InvalidOperationException(string.Format("Duplicate patches with key '{0}'", currentPatchKey));
                        }

                        Console.WriteLine("[TinyIL] Located patch '{0}'", currentPatchKey);
                        output.Add(currentPatchKey, patchBuilder.ToString());
                        patchBuilder.Length = 0;
                        currentPatchKey = null;
                        modified = true;
                    }
                }
            }

            return modified;
        }
    }

    /// <summary>
    /// Delegate for processing and modifying a type.
    /// </summary>
    /// <returns>Number of members modified.</returns>
    public delegate int ProcessTypeDelegate(TypeDefinition typeDef, ref PatchFileCache patchCache, object context);

    /// <summary>
    /// Delegate for processing and modifying a method.
    /// </summary>
    /// <returns>If the method was modified.</returns>
    public delegate bool ProcessMethodDelegate(MethodDefinition methodDef, ref PatchFileCache patchCache, object context);
}

#endif // UNITY_EDITOR