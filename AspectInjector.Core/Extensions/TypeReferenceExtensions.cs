﻿using AspectInjector.Core.Models;
using Mono.Cecil;
using System;

namespace AspectInjector.Core.Extensions
{
    public static class TypeReferenceExtensions
    {
        public static bool IsTypeOf(this TypeReference tr1, TypeReference tr2)
        {
            return FQN.FromTypeReference(tr1).Equals(FQN.FromTypeReference(tr2));
        }

        public static bool IsTypeOf(this TypeReference tr, Type type)
        {
            return FQN.FromTypeReference(tr).Equals(FQN.FromType(type));
        }

        public static FQN GetFQN(this TypeReference type)
        {
            return FQN.FromTypeReference(type);
        }
    }
}