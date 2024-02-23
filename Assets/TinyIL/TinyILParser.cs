#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace TinyIL {
    static public class TinyILParser {
        #region Types

        /// <summary>
        /// Exception for an invalid IL code.
        /// </summary>
        public class InvalidILException : Exception {
            public InvalidILException(string message) : base(message) { }
            public InvalidILException(string format, params object[] args) : base(string.Format(format, args)) { }
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

            public MethodContext(MethodDefinition definition) {
                Definition = definition;
                Body = definition.Body;
                Processor = Body.GetILProcessor();
                VarNames = new List<string>(8);
                Labels = new List<LabelDefinition>(8);
                Branches = new List<LateBranchResolver>(8);
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
            MethodContext context = new MethodContext(methodDefinition);
            PrepareOverwrite(context);
            ProcessLines(context, ilBlock);
            PostProcess(context);
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

            if (string.IsNullOrEmpty(ilLine)) {
                return;
            }

            string op, operand;
            int spaceIdx = ilLine.IndexOf(' ');
            if (spaceIdx >= 0) {
                op = ilLine.Substring(0, spaceIdx).Trim();
                operand = ilLine.Substring(spaceIdx + 1).Trim();
            } else {
                op = ilLine;
                operand = null;
            }

            if (operand == null && op.EndsWith(":")) {
                DefineLabel(context, op.Substring(1));
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
                Instruction inst = generator(operand, context);
                context.Processor.Append(inst);
            } else {
                throw new InvalidILException("Unrecognized or unsupported IL opcode '{0}'", op);
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

        static private TypeReference FindType(MethodContext context, string typeName) {
            // local only to this function
            TypeReference SearchGenerics(IGenericParameterProvider provider, string name) {
                if (provider.HasGenericParameters) {
                    foreach (var param in provider.GenericParameters) {
                        if (param.Name.Equals(name, StringComparison.Ordinal)) {
                            return param;
                        }
                    }
                }

                return null;
            }

            if (string.IsNullOrEmpty(typeName)) {
                throw new ArgumentNullException("typeName");
            }

            // TODO: Handle pointers

            TypeReference typeRef;

            switch (typeName) {
                case "int64": {
                    return context.Definition.Module.TypeSystem.Int64;
                }
                case "int32": {
                    return context.Definition.Module.TypeSystem.Int32;
                }
                case "int16": {
                    return context.Definition.Module.TypeSystem.Int16;
                }
                case "int8": {
                    return context.Definition.Module.TypeSystem.SByte;
                }
                case "uint64": {
                    return context.Definition.Module.TypeSystem.UInt64;
                }
                case "uint32": {
                    return context.Definition.Module.TypeSystem.UInt32;
                }
                case "uint16": {
                    return context.Definition.Module.TypeSystem.UInt16;
                }
                case "uint8": {
                    return context.Definition.Module.TypeSystem.Byte;
                }
                case "bool": {
                    return context.Definition.Module.TypeSystem.Boolean;
                }
                case "float": {
                    return context.Definition.Module.TypeSystem.Single;
                }
                case "double": {
                    return context.Definition.Module.TypeSystem.Double;
                }
                case "string": {
                    return context.Definition.Module.TypeSystem.String;
                }
                case "char": {
                    return context.Definition.Module.TypeSystem.Char;
                }
                case "object": {
                    return context.Definition.Module.TypeSystem.Object;
                }
                case "void": {
                    return context.Definition.Module.TypeSystem.Void;
                }
            }

            if (typeName.StartsWith("!!")) {
                // generic
                typeName = typeName.Substring(2);

                typeRef = SearchGenerics(context.Definition, typeName);
                if (typeRef != null) {
                    return typeRef;
                }

                TypeDefinition declaringType = context.Definition.DeclaringType;
                while(declaringType != null && typeRef == null) {
                    typeRef = SearchGenerics(context.Definition, typeName);
                    declaringType = declaringType.DeclaringType;
                }

                if (typeRef == null) {
                    throw new InvalidILException("Unable to locate generic type with name '{0}'", typeName);
                }

                return typeRef;
            }

            if (typeName.StartsWith("[") && typeName.EndsWith("]")) {
                typeName = typeName.Substring(1, typeName.Length - 2).Trim();
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
            }

            typeRef = null;

            foreach (var module in context.Modules) {
                if (module.TryGetTypeReference(typeName, out typeRef)) {
                    break;
                }
            }

            if (typeRef == null) {
                throw new InvalidILException("Unable to locate type with name '{0}'", typeName);
            }

            return context.Definition.Module.ImportReference(typeRef);
        }

        static private ParameterDefinition FindParam(MethodContext context, string paramName) {
            if (string.IsNullOrEmpty(paramName)) {
                throw new ArgumentNullException("paramName");
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
                throw new ArgumentNullException("varName");
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
                throw new ArgumentNullException("labelName");
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

        static private CallSite ParseCallSite(MethodContext context, string descriptor) {
            // TODO: Implement
            throw new NotImplementedException();
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
            //{ "calli", (a, d) => { return Instruction.Create(OpCodes.Calli, ParseCallSite(d, a)); } },
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
            { "ldc.i4", (a, d) => { return Instruction.Create(OpCodes.Ldc_I4, Convert.ToInt32(a)); } },
            { "ldc.i4.s", (a, d) => { return Instruction.Create(OpCodes.Ldc_I4_S, Convert.ToByte(a)); } },
            { "ldc.i8", (a, d) => { return Instruction.Create(OpCodes.Ldc_I8, Convert.ToInt64(a)); } },
            { "ldc.r4", (a, d) => { return Instruction.Create(OpCodes.Ldc_R4, Convert.ToSingle(a)); } },
            { "ldc.r8", (a, d) => { return Instruction.Create(OpCodes.Ldc_R8, Convert.ToDouble(a)); } },
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
            { "unaligned.", (a, d) => { return Instruction.Create(OpCodes.Unaligned, Convert.ToByte(a)); } },
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

        #endregion // Utils

        #region Readers

        //private struct LineReader {
        //    public string Line;
        //    public int CurrentIndex;

        //    public void Initialize(string line) {
        //        Line = line;
        //        CurrentIndex = 0;
        //    }

        //    public void SkipWhitespace() {
        //        while(CurrentIndex < Line.Length && char.IsWhiteSpace(Line[CurrentIndex])) {
        //            CurrentIndex++;
        //        }
        //    }

        //    public bool IsEOL() {
        //        return Line == null || CurrentIndex >= Line.Length;
        //    }
        //}

        #endregion // Readers
    }
}

#endif // UNITY_EDITOR