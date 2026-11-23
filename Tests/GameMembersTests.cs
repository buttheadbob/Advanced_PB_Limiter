using System;
using System.Reflection;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class GameMembersTests
    {
        // Stands in for a game type: a private field and overloads, as the limiter finds them in MyProgrammableBlock.
        private sealed class Sample
        {
            private int _value;

            public int Value => _value;

            public void Overloaded(int number) => _value = number;

            public void Overloaded(string text) => _value = text.Length;

            public void Once() => _value = 1;
        }

        [Fact]
        public void TheSampleWorksWhenCalledDirectly()
        {
            Sample sample = new();
            sample.Overloaded(3);
            Assert.Equal(3, sample.Value);
            sample.Overloaded("two");
            Assert.Equal(3, sample.Value);
            sample.Once();
            Assert.Equal(1, sample.Value);
        }

        [Fact]
        public void FindsAPrivateInstanceField()
        {
            FieldInfo field = GameMembers.Field(typeof(Sample), "_value");

            Assert.Equal(typeof(int), field.FieldType);
        }

        [Fact]
        public void FindsTheOverloadWithTheseParameters()
        {
            MethodInfo method = GameMembers.Method(typeof(Sample), "Overloaded", typeof(string));
            Sample sample = new();
            method.Invoke(sample, new object[] { "four" });

            Assert.Equal(4, sample.Value);
        }

        [Fact]
        public void OnlyMethodFindsAMethodWithNoOverloads()
        {
            Assert.Equal("Once", GameMembers.OnlyMethod(typeof(Sample), "Once").Name);
        }

        [Fact]
        public void OnlyMethodRefusesOverloads()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GameMembers.OnlyMethod(typeof(Sample), "Overloaded"));

            Assert.Contains("overloads", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMissingMemberNamesIt()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => GameMembers.Field(typeof(Sample), "m_gone"));

            Assert.Contains("Sample.m_gone", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadyRefusesAMemberNotYetLookedUp()
        {
            FieldInfo? notLookedUp = null;

            Assert.Throws<InvalidOperationException>(() => GameMembers.Ready(notLookedUp));
        }
    }
}
