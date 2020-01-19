﻿using AspectInjector.Broker;
using Xunit;
using System;
using System.Text;

namespace AspectInjector.Tests.Advices
{
    
    public class StaticTests
    {
        [Fact]
        public void Advices_InjectBeforeStaticMethod()
        {
            Checker.Passed = false;
            StaticTests_BeforeTarget.TestStaticMethod();
            Assert.True(Checker.Passed);
        }

        [Fact]
        public void Advices_InjectAroundStaticMethod()
        {
            Checker.Passed = false;

            int v = 2;
            object vv = v;

            StaticTests_AroundTarget.Do123((System.Int32)vv, new StringBuilder(), new object(), false, false);

            Assert.True(Checker.Passed);
        }

        public class StaticTests_AroundTarget
        {
            [StaticTests_AroundAspect1]
            [StaticTests_AroundAspect2] //fire first
            public static int Do123(int data, StringBuilder sb, object to, bool passed, bool passed2)
            {
                Checker.Passed = passed && passed2;

                var a = 1;
                var b = a + data;
                return b;
            }
        }

        [Aspect(Scope.Global)]
        [Injection(typeof(StaticTests_AroundAspect1))]
        public class StaticTests_AroundAspect1 : Attribute
        {
            [Advice(Kind.Around, Targets = Target.Method)]
            public object AroundMethod([Argument(Source.Target)] Func<object[], object> target,
                [Argument(Source.Arguments)] object[] arguments)
            {
                return target(new object[] { arguments[0], arguments[1], arguments[2], true, arguments[4] });
            }
        }

        [Aspect(Scope.Global)]
        [Injection(typeof(StaticTests_AroundAspect2))]
        public class StaticTests_AroundAspect2 : Attribute
        {
            [Advice(Kind.Around, Targets = Target.Method)]
            public object AroundMethod([Argument(Source.Target)] Func<object[], object> target,
                [Argument(Source.Arguments)] object[] arguments)
            {
                return target(new object[] { arguments[0], arguments[1], arguments[2], arguments[3], true });
            }
        }

        [StaticTests_BeforeAspect]
        internal class StaticTests_BeforeTarget
        {
            public static void TestStaticMethod()
            {
            }

            public void TestInstanceMethod()
            {
            }
        }

        [Aspect(Scope.Global)]
        [Injection(typeof(StaticTests_BeforeAspect))]
        public class StaticTests_BeforeAspect : Attribute
        {
            //Property
            [Advice(Kind.Before, Targets = Target.Method)]
            public void BeforeMethod() { Checker.Passed = true; }
        }
    }
}