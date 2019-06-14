﻿using AspectInjector.Core.Contracts;
using AspectInjector.Core.Extensions;
using AspectInjector.Core.Models;
using AspectInjector.Rules;
using FluentIL.Extensions;
using FluentIL.Logging;
using Mono.Cecil;
using System.Collections.Generic;
using System.Linq;

namespace AspectInjector.Core.Services
{
    public class InjectionReader : IInjectionReader
    {
        private readonly IAspectReader _aspectReader;
        private readonly ILogger _log;

        public InjectionReader(IAspectReader aspectReader, ILogger logger)
        {
            _aspectReader = aspectReader;
            _log = logger;
        }

        public IReadOnlyCollection<InjectionDefinition> ReadAll(AssemblyDefinition assembly)
        {
            var aspects = ExtractInjections(assembly);

            foreach (var module in assembly.Modules)
            {
                aspects = aspects.Concat(ExtractInjections(module));

                foreach (var type in module.GetTypes())
                {
                    aspects = aspects.Concat(ExtractInjections(type));

                    aspects = aspects.Concat(type.Events.SelectMany(ExtractInjections));
                    aspects = aspects.Concat(type.Properties.SelectMany(ExtractInjections));
                    //aspects = aspects.Concat(type.Fields.SelectMany(ExtractInjections));
                    aspects = aspects.Concat(type.Methods.SelectMany(ExtractInjections));
                }
            }

            aspects = aspects.GroupBy(a => a).Select(g => g.Aggregate(MergeInjections)).ToList();

            return aspects.ToList();
        }

        //private IReadOnlyCollection<InjectionDefinition> ReadAll(TypeDefinition type)
        //{
        //    var aspects = ExtractInjections(type);

        //    aspects = aspects.Concat(type.Events.SelectMany(ExtractInjections));
        //    aspects = aspects.Concat(type.Properties.SelectMany(ExtractInjections));
        //    //aspects = aspects.Concat(type.Fields.SelectMany(ExtractInjections));
        //    aspects = aspects.Concat(type.Methods.SelectMany(ExtractInjections));

        //    return aspects.ToArray();
        //}

        protected virtual IEnumerable<InjectionDefinition> ExtractInjections(ICustomAttributeProvider target)
        {
            var injections = Enumerable.Empty<InjectionDefinition>();

            foreach (var trigger in target.CustomAttributes
                .Select(a => new { attribute = a, injections = a.AttributeType.Resolve().CustomAttributes.Where(t => t.AttributeType.FullName == WellKnownTypes.Injection).ToArray() })
                .Where(e => e.injections.Length != 0).ToArray())
                injections = injections.Concat(ParseInjectionAttribute(target, trigger.attribute, trigger.injections));


            return injections;
        }

        private IEnumerable<InjectionDefinition> ParseInjectionAttribute(ICustomAttributeProvider target, CustomAttribute trigger, CustomAttribute[] injectionAttrs)
        {
            var injections = Enumerable.Empty<InjectionDefinition>();

            foreach (var injectionAttr in injectionAttrs)
            {
                var aspectRef = injectionAttr.GetConstructorValue<TypeReference>(0);
                var aspect = _aspectReader.Read(aspectRef.Resolve());

                if (aspect == null)
                {
                    _log.Log(InjectionRules.InjectionMustReferToAspect, target, aspectRef.Name);
                    continue;
                }

                var priority = injectionAttr.GetPropertyValue<ushort>(nameof(Broker.Injection.Priority));

                injections = injections.Concat(FindApplicableMembers(target, aspect, priority, trigger));
            }

            return injections;
        }

        private IEnumerable<InjectionDefinition> FindApplicableMembers(ICustomAttributeProvider target, AspectDefinition aspect, ushort priority, CustomAttribute trigger)
        {
            var result = Enumerable.Empty<InjectionDefinition>();

            if (target is AssemblyDefinition assm)
                result = result.Concat(assm.Modules.SelectMany(nt => FindApplicableMembers(nt, aspect, priority, trigger)));

            if (target is ModuleDefinition module)
                result = result.Concat(module.Types.SelectMany(nt => FindApplicableMembers(nt, aspect, priority, trigger)));

            if (target is IMemberDefinition member)
                result = result.Concat(CreateInjections(member, aspect, priority, trigger));

            if (target is TypeDefinition type)
            {
                result = result.Concat(type.Methods.Where(m => m.IsNormalMethod() || m.IsConstructor)
                    .SelectMany(m => FindApplicableMembers(m, aspect, priority, trigger)));
                result = result.Concat(type.Events.SelectMany(m => FindApplicableMembers(m, aspect, priority, trigger)));
                result = result.Concat(type.Properties.SelectMany(m => FindApplicableMembers(m, aspect, priority, trigger)));
                result = result.Concat(type.NestedTypes.Where(nt => !nt.IsCompilerGenerated()).SelectMany(nt => FindApplicableMembers(nt, aspect, priority, trigger)));
            }

            return result;
        }

        private IEnumerable<InjectionDefinition> CreateInjections(IMemberDefinition target, AspectDefinition aspect, ushort priority, CustomAttribute trigger)
        {
            if (IsAspectMember(target)) return Enumerable.Empty<InjectionDefinition>();

            return aspect.Effects.Where(e => e.IsApplicableFor(target)).Select(e => new InjectionDefinition()
            {
                Target = target,
                Source = aspect,
                Priority = priority,
                Effect = e,
                Triggers = new List<CustomAttribute> { trigger }
            });
        }

        private bool IsAspectMember(IMemberDefinition member)
        {
            if (member == null)
                return false;

            if (member is TypeDefinition type && _aspectReader.Read(type) != null)
                return true;

            return IsAspectMember(member.DeclaringType);
        }

        private InjectionDefinition MergeInjections(InjectionDefinition a1, InjectionDefinition a2)
        {
            a1.Priority = Enumerable.Max(new[] { a1.Priority, a2.Priority });
            a1.Triggers = a1.Triggers.Concat(a2.Triggers).Distinct().ToList();
            return a1;
        }

        //private static bool CheckFilter(MethodDefinition targetMethod,
        //    string targetName,
        //    ChildrenFilter aspectDefinition)
        //{
        //    var result = true;

        //    var nameFilter = aspectDefinition.NameFilter;
        //    var accessModifierFilter = aspectDefinition.AccessModifierFilter;

        //    if (!string.IsNullOrEmpty(nameFilter))
        //    {
        //        result = Regex.IsMatch(targetName, nameFilter);
        //    }

        //    if (result && accessModifierFilter != AccessModifier.Any)
        //    {
        //        if (targetMethod.IsPrivate)
        //        {
        //            result = (accessModifierFilter & AccessModifier.Private) != 0;
        //        }
        //        else if (targetMethod.IsFamily)
        //        {
        //            result = (accessModifierFilter & AccessModifier.Protected) != 0;
        //        }
        //        else if (targetMethod.IsAssembly)
        //        {
        //            result = (accessModifierFilter & AccessModifier.Internal) != 0;
        //        }
        //        else if (targetMethod.IsFamilyOrAssembly)
        //        {
        //            result = (accessModifierFilter & AccessModifier.ProtectedInternal) != 0;
        //        }
        //        else if (targetMethod.IsPublic)
        //        {
        //            result = (accessModifierFilter & AccessModifier.Public) != 0;
        //        }
        //    }

        //    return result;
        //}
    }
}