﻿using AspectInjector.Broker;
using AspectInjector.BuildTask.Common;
using AspectInjector.BuildTask.Extensions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AspectInjector.BuildTask.Contexts
{
    public class TargetMethodContext
    {
        private static readonly string _exceptionVariableName = "__a$_exception";
        private static readonly string _methodResultVariableName = "__a$_method_result";

        private Instruction _exceptionPoint;
        private VariableDefinition _exceptionVar;
        private VariableDefinition _resultVar;

        public TargetMethodContext(MethodDefinition targetMethod)
        {
            TargetMethod = targetMethod;
            Processor = TargetMethod.Body.GetILProcessor();

            SetupEntryPoints();
            SetupReturnPoints();
        }

        public Instruction EntryPoint { get; private set; }

        public Instruction ExceptionPoint
        {
            get
            {
                if (_exceptionPoint == null)
                    SetupCatchBlock();

                return _exceptionPoint;
            }
        }

        public VariableDefinition ExceptionVariable
        {
            get
            {
                if (_exceptionVar == null)
                    SetupCatchBlock();

                return _exceptionVar;
            }
        }

        public Instruction ExitPoint { get; private set; }

        public VariableDefinition MethodResultVariable
        {
            get
            {
                if (TargetMethod.ReturnType.IsTypeOf(typeof(void)))
                    return null;

                if (_resultVar == null)
                {
                    _resultVar = new VariableDefinition(_methodResultVariableName, TargetMethod.ReturnType);
                    Processor.Body.Variables.Add(_resultVar);
                    Processor.Body.InitLocals = true;
                }

                return _resultVar;
            }
        }

        public Instruction OriginalCodeReturnPoint { get; private set; }

        public Instruction OriginalEntryPoint { get; private set; }

        public ILProcessor Processor { get; private set; }

        public Instruction ReturnPoint { get; private set; }

        public MethodDefinition TargetMethod { get; private set; }

        private void SetupCatchBlock()
        {
            var exceptionType = TargetMethod.Module.TypeSystem.ResolveType(typeof(Exception));
            _exceptionVar = new VariableDefinition(_exceptionVariableName, exceptionType);
            Processor.Body.Variables.Add(_exceptionVar);
            Processor.Body.InitLocals = true;

            var setVarInst = Processor.SafeInsertAfter(OriginalCodeReturnPoint, Processor.CreateOptimized(OpCodes.Stloc, _exceptionVar.Index));
            _exceptionPoint = Processor.SafeInsertAfter(setVarInst, Processor.Create(OpCodes.Rethrow));

            OriginalCodeReturnPoint = Processor.SafeReplace(OriginalCodeReturnPoint, Processor.Create(OpCodes.Leave, ReturnPoint)); //todo:: optimize

            Processor.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = OriginalEntryPoint,
                TryEnd = OriginalCodeReturnPoint.Next,
                HandlerStart = OriginalCodeReturnPoint.Next,
                HandlerEnd = _exceptionPoint.Next,
                CatchType = exceptionType
            });
        }

        private void SetupEntryPoints()
        {
            OriginalEntryPoint = TargetMethod.IsConstructor && !TargetMethod.IsStatic ?
                TargetMethod.FindBaseClassCtorCall() :
                TargetMethod.Body.Instructions.First();

            EntryPoint = Processor.SafeInsertBefore(OriginalEntryPoint, Processor.Create(OpCodes.Nop));
        }

        private void SetupReturnPoints()
        {
            ReturnPoint = Processor.Create(OpCodes.Nop);

            OriginalCodeReturnPoint = SetupSingleReturnPoint(Processor.Create(OpCodes.Br, ReturnPoint));//todo:: optimize
            Processor.SafeAppend(ReturnPoint);

            if (!TargetMethod.ReturnType.IsTypeOf(typeof(void)))
            {
                ExitPoint = Processor.SafeAppend(Processor.CreateOptimized(OpCodes.Ldloc, MethodResultVariable.Index));
                Processor.SafeAppend(Processor.Create(OpCodes.Ret));
            }
            else
            {
                ExitPoint = Processor.SafeAppend(Processor.Create(OpCodes.Ret));
            }
        }

        private Instruction SetupSingleReturnPoint(Instruction suggestedSingleReturnPoint)
        {
            var rets = Processor.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();

            if (rets.Count == 1)
            {
                if (!TargetMethod.ReturnType.IsTypeOf(typeof(void)))
                    Processor.SafeInsertBefore(rets.First(), Processor.CreateOptimized(OpCodes.Stloc, MethodResultVariable.Index));

                return Processor.SafeReplace(rets.First(), suggestedSingleReturnPoint);
            }

            foreach (var i in rets)
            {
                if (!TargetMethod.ReturnType.IsTypeOf(typeof(void)))
                    Processor.SafeInsertBefore(rets.First(), Processor.CreateOptimized(OpCodes.Stloc, MethodResultVariable.Index));

                Processor.SafeReplace(i, Processor.Create(OpCodes.Br, suggestedSingleReturnPoint)); //todo:: optimize
            }

            Processor.SafeAppend(suggestedSingleReturnPoint);

            return suggestedSingleReturnPoint;
        }

        public void LoadFieldOntoStack(Instruction injectionPoint, FieldReference field)
        {
            if (field.Resolve().IsStatic)
            {
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldsfld, field));
            }
            else
            {
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldarg_0));
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldfld, field));
            }
        }

        public void SetFieldFromStack(Instruction injectionPoint, FieldReference field, Action loadValueToStack)
        {
            if (field.Resolve().IsStatic)
            {
                loadValueToStack();
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Stsfld, field));
            }
            else
            {
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldarg_0));
                loadValueToStack();
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Stfld, field));
            }
        }

        public void InjectMethodCall(Instruction injectionPoint, MemberReference sourceMember, MethodDefinition method, object[] arguments)
        {
            if (method.Parameters.Count != arguments.Length)
                throw new ArgumentException("Arguments count mismatch", "arguments");

            //processor.InsertBefore(injectionPoint, processor.Create(OpCodes.Nop));

            var type = Processor.Body.Method.DeclaringType;

            if (sourceMember is FieldReference)
            {
                LoadFieldOntoStack(injectionPoint, (FieldReference)sourceMember);
            }
            else if (sourceMember == null)
            {
                if (method.IsStatic)
                {
                    //Nothing should be loaded onto stack
                }
                else
                {
                    if (method.DeclaringType == TargetMethod.DeclaringType)
                        Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldarg_0));
                }
            }
            else
            {
                throw new NotSupportedException("Only FieldRefences and nulls supported at the moment");
            }

            for (int i = 0; i < method.Parameters.Count; i++)
                LoadCallArgument(injectionPoint, arguments[i], method.Parameters[i].ParameterType);

            OpCode code;

            if (method.IsConstructor)
                code = OpCodes.Newobj;
            else if (method.IsVirtual)
                code = OpCodes.Callvirt;
            else
                code = OpCodes.Call;

            Processor.InsertBefore(injectionPoint, Processor.Create(code, Processor.Body.Method.Module.Import(method)));
        }

        protected void LoadCallArgument(Instruction injectionPoint, object arg, TypeReference expectedType)
        {
            var module = Processor.Body.Method.Module;

            if (arg is ParameterDefinition)
            {
                var parameter = (ParameterDefinition)arg;

                Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(OpCodes.Ldarg, parameter.Index + 1));

                if (parameter.ParameterType.IsValueType && expectedType.IsTypeOf(module.TypeSystem.Object))
                    Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Box, module.Import(parameter.ParameterType)));
            }
            else if (arg is VariableDefinition)
            {
                var var = (VariableDefinition)arg;

                if (!expectedType.IsTypeOf(module.TypeSystem.Object) && !expectedType.Resolve().IsTypeOf(var.VariableType))
                    throw new ArgumentException("Argument type mismatch");

                Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(expectedType.IsByReference ? OpCodes.Ldloca : OpCodes.Ldloc, var.Index));

                if (var.VariableType.IsValueType && expectedType.IsTypeOf(module.TypeSystem.Object))
                    Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Box, module.Import(var.VariableType)));
            }
            else if (arg is string)
            {
                if (!expectedType.IsTypeOf(module.TypeSystem.Object) && !expectedType.IsTypeOf(module.TypeSystem.String))
                    throw new ArgumentException("Argument type mismatch");

                var str = (string)arg;
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldstr, str));
            }
            else if (arg is CustomAttributeArgument)
            {
                var caa = (CustomAttributeArgument)arg;

                if (caa.Type.IsArray)
                {
                    if (!expectedType.IsTypeOf(module.TypeSystem.Object) &&
                        !expectedType.IsTypeOf(new ArrayType(module.TypeSystem.Object))
                        )
                        throw new ArgumentException("Argument type mismatch");

                    LoadArray(injectionPoint, caa.Value, caa.Type.GetElementType(), expectedType);
                }
                else if (caa.Value is CustomAttributeArgument || caa.Type.IsTypeOf(module.TypeSystem.String))
                {
                    LoadCallArgument(injectionPoint, caa.Value, expectedType);
                }
                else
                {
                    LoadValueTypedArgument(injectionPoint, Processor, caa.Value, caa.Type, expectedType);
                }
            }
            else if (arg == Markers.InstanceSelfMarker)
            {
                if (!expectedType.IsTypeOf(module.TypeSystem.Object))
                    throw new ArgumentException("Argument type mismatch");

                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldarg_0));
            }
            else if (arg == Markers.DefaultMarker)
            {
                if (!expectedType.IsTypeOf(module.TypeSystem.Void))
                {
                    if (expectedType.IsValueType)
                        Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldc_I4_0));
                    else
                        Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldnull));
                }
            }
            else if (arg is TypeReference)
            {
                var typeOfType = module.TypeSystem.ResolveType(typeof(Type));

                if (!expectedType.IsTypeOf(module.TypeSystem.Object) && !expectedType.IsTypeOf(typeOfType))
                    throw new ArgumentException("Argument type mismatch");

                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Ldtoken, (TypeReference)arg));
                Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Call, module.Import(typeOfType.Resolve().Methods.First(m => m.Name == "GetTypeFromHandle"))));
            }
            else if (arg.GetType().IsValueType)
            {
                var type = module.TypeSystem.ResolveType(arg.GetType());
                LoadValueTypedArgument(injectionPoint, Processor, arg, type, expectedType);
            }
            else if (arg is Array)
            {
                var elementType = arg.GetType().GetElementType();

                if (elementType == typeof(ParameterDefinition))
                    elementType = typeof(object);

                LoadArray(injectionPoint, arg, module.TypeSystem.ResolveType(elementType), expectedType);
            }
            else
            {
                throw new NotSupportedException("Argument type of " + arg.GetType().ToString() + " is not supported");
            }
        }

        private void LoadArray(Instruction injectionPoint, object args, TypeReference targetElementType, TypeReference expectedType)
        {
            var module = Processor.Body.Method.Module;

            if (!expectedType.IsTypeOf(module.TypeSystem.Object) && !expectedType.IsTypeOf(new ArrayType(module.TypeSystem.Object)))
                throw new ArgumentException("Argument type mismatch");

            var parameters = ((Array)args).Cast<object>().ToArray();

            var elementType = module.Import(targetElementType.Resolve());

            Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(OpCodes.Ldc_I4, parameters.Length));
            Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Newarr, elementType));

            if (parameters.Length > 0)
            {
                Processor.Body.InitLocals = true;

                var paramsArrayVar = new VariableDefinition(new ArrayType(elementType));
                Processor.Body.Variables.Add(paramsArrayVar);

                Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(OpCodes.Stloc, paramsArrayVar.Index));

                for (var i = 0; i < parameters.Length; i++)
                {
                    Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(OpCodes.Ldloc, paramsArrayVar.Index));
                    Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(OpCodes.Ldc_I4, i));

                    LoadCallArgument(injectionPoint, parameters[i], module.TypeSystem.Object);

                    Processor.InsertBefore(injectionPoint, Processor.Create(OpCodes.Stelem_Ref));
                }

                Processor.InsertBefore(injectionPoint, Processor.CreateOptimized(OpCodes.Ldloc, paramsArrayVar.Index));
            }
        }

        private void LoadValueTypedArgument(Instruction injectionPoint, ILProcessor processor, object arg, TypeReference type, TypeReference expectedType)
        {
            if (!arg.GetType().IsValueType)
                throw new NotSupportedException("Only value types are supported.");

            var module = processor.Body.Method.Module;

            if (!expectedType.IsTypeOf(module.TypeSystem.Object) && !expectedType.IsTypeOf(type))
                throw new ArgumentException("Argument type mismatch");

            if (arg is long || arg is ulong || arg is double)
            {
                var rawdata = GetRawValueType(arg, 8);
                var val = BitConverter.ToInt64(rawdata, 0);

                processor.InsertBefore(injectionPoint, processor.Create(OpCodes.Ldc_I8, val));
            }
            else
            {
                var rawdata = GetRawValueType(arg, 4);
                var val = BitConverter.ToInt32(rawdata, 0);

                processor.InsertBefore(injectionPoint, processor.CreateOptimized(OpCodes.Ldc_I4, val));
            }

            if (expectedType.IsTypeOf(module.TypeSystem.Object))
                processor.InsertBefore(injectionPoint, processor.Create(OpCodes.Box, module.Import(type)));
        }

        private byte[] GetRawValueType(object value, int @base = 0)
        {
            byte[] rawdata = new byte[@base == 0 ? Marshal.SizeOf(value) : @base];

            GCHandle handle = GCHandle.Alloc(rawdata, GCHandleType.Pinned);
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            handle.Free();

            return rawdata;
        }
    }
}