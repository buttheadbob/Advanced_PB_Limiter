using System;
using System.IO;
using System.Xml;
using Advanced_PB_Limiter.Utils;
using Xunit;

namespace Advanced_PB_Limiter.Tests
{
    public sealed class ReadFailureTests
    {
        [Fact]
        public void AnIoErrorIsAReadFailure()
        {
            Assert.True(SafeFile.IsReadFailure(new IOException("held")));
            Assert.True(SafeFile.IsReadFailure(new UnauthorizedAccessException("denied")));
        }

        [Fact]
        public void AnIoErrorWrappedByXmlSerializerIsAReadFailure()
        {
            // XmlSerializer.Deserialize wraps what the stream throws part way through the read.
            Assert.True(SafeFile.IsReadFailure(new InvalidOperationException("There is an error in XML document (3, 7).", new IOException("The network name is no longer available."))));
        }

        [Fact]
        public void AnIoErrorWrappedByNestedSerializersIsAReadFailure()
        {
            // A config whose properties have serializers of their own wraps it once per serializer.
            Exception inner = new InvalidOperationException("There is an error in XML document (40, 3).", new IOException("The device is not ready."));
            Assert.True(SafeFile.IsReadFailure(new InvalidOperationException("There is an error in XML document (2, 2).", inner)));
        }

        [Fact]
        public void ABrokenDocumentIsNotAReadFailure()
        {
            // These set the config aside as unreadable.
            Assert.False(SafeFile.IsReadFailure(new InvalidOperationException("There is an error in XML document (1, 1).", new XmlException("Root element is missing."))));
            Assert.False(SafeFile.IsReadFailure(new InvalidOperationException("There is an error in XML document (1, 1).")));
            Assert.False(SafeFile.IsReadFailure(new InvalidDataException("The file holds no config.")));
        }
    }
}
