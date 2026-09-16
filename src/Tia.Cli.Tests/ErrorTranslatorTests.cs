using System;
using TiaCli.Protocol;
using Xunit;

namespace TiaCli.Tests
{
    // ErrorTranslator matches Openness exceptions by type NAME so it never references the Siemens
    // assembly. These stand-ins exercise those paths the same way the real types would.
    internal sealed class LicenseNotFoundException : Exception
    {
        public LicenseNotFoundException(string message) : base(message) { }
    }

    internal sealed class EngineeringSecurityException : Exception
    {
        public EngineeringSecurityException(string message) : base(message) { }
    }

    internal sealed class NonRecoverableException : Exception
    {
        public NonRecoverableException(string message) : base(message) { }
    }

    internal sealed class SessionException : Exception
    {
        public string Code { get; }
        public string Hint { get; }
        public SessionException(string code, string message, string hint) : base(message)
        {
            Code = code;
            Hint = hint;
        }
    }

    public class ErrorTranslatorDescribeTests
    {
        [Fact]
        public void WireExceptionPassesThroughUnchanged()
        {
            var error = ErrorTranslator.Describe(
                new WireException(WireErrorCodes.NotFound, "no such block", hint: "try 'tia blocks'", detail: "d"));
            Assert.Equal(WireErrorCodes.NotFound, error.Code);
            Assert.Equal("no such block", error.Message);
            Assert.Equal("try 'tia blocks'", error.Hint);
            Assert.Equal("d", error.Detail);
        }

        [Fact]
        public void SingleInnerAggregateIsUnwrapped()
        {
            var error = ErrorTranslator.Describe(
                new AggregateException(new WireException(WireErrorCodes.Ambiguous, "two portals")));
            Assert.Equal(WireErrorCodes.Ambiguous, error.Code);
            Assert.Equal("two portals", error.Message);
        }

        [Fact]
        public void MultiInnerAggregateIsNotUnwrapped()
        {
            var error = ErrorTranslator.Describe(new AggregateException(
                new WireException(WireErrorCodes.NotFound, "a"),
                new WireException(WireErrorCodes.NotFound, "b")));
            Assert.Equal(WireErrorCodes.Internal, error.Code);
        }

        [Fact]
        public void SessionExceptionIsMatchedStructurally()
        {
            var error = ErrorTranslator.Describe(
                new SessionException(WireErrorCodes.NotConnected, "no portal", "start one"));
            Assert.Equal(WireErrorCodes.NotConnected, error.Code);
            Assert.Equal("no portal", error.Message);
            Assert.Equal("start one", error.Hint);
        }

        [Fact]
        public void LicenseFailureIsFoundDownTheInnerChain()
        {
            var error = ErrorTranslator.Describe(new InvalidOperationException("call failed",
                new LicenseNotFoundException("no STEP 7 licence")));
            Assert.Equal(WireErrorCodes.LicenseMissing, error.Code);
            Assert.Equal("no STEP 7 licence", error.Message);
            Assert.Equal("call failed", error.Detail);
        }

        [Fact]
        public void SecurityExceptionBecomesAccessDenied()
        {
            var error = ErrorTranslator.Describe(new EngineeringSecurityException("not permitted"));
            Assert.Equal(WireErrorCodes.AccessDenied, error.Code);
            Assert.Contains("Siemens TIA Openness", error.Hint);
        }

        [Fact]
        public void NonRecoverableBecomesPortalUnrecoverable()
        {
            var error = ErrorTranslator.Describe(new NonRecoverableException("portal died"));
            Assert.Equal(WireErrorCodes.PortalUnrecoverable, error.Code);
        }

        [Fact]
        public void DisposedPortalHandleBecomesPortalUnrecoverable()
        {
            var error = ErrorTranslator.Describe(new ObjectDisposedException("TiaPortal"));
            Assert.Equal(WireErrorCodes.PortalUnrecoverable, error.Code);
        }

        [Fact]
        public void AnythingElseIsInternalWithTheTypeAsDetail()
        {
            var error = ErrorTranslator.Describe(new InvalidOperationException("boom"));
            Assert.Equal(WireErrorCodes.Internal, error.Code);
            Assert.Equal("boom", error.Message);
            Assert.Equal(typeof(InvalidOperationException).FullName, error.Detail);
        }
    }

    public class PasswordPolicyTests
    {
        // Nested is fine: ErrorTranslator matches on the end of the type's full name.
        internal sealed class EngineeringPasswordPolicyViolationException : Exception
        {
            public EngineeringPasswordPolicyViolationException(string message) : base(message) { }
        }

        [Fact]
        public void PolicyViolationSaysItWasThePassword()
        {
            // TIA reports only which method failed, so the type name is the whole clue.
            var error = ErrorTranslator.Describe(new EngineeringPasswordPolicyViolationException(
                "Error when calling method 'SetPassword' of type 'PlcAccessLevelProvider'."));

            Assert.Equal(WireErrorCodes.InvalidRequest, error.Code);
            Assert.Contains("password policy", error.Message);
            Assert.Contains("special character", error.Hint);
            Assert.Contains("SetPassword", error.Detail);
        }

        [Fact]
        public void PolicyViolationIsNotJustAnotherOpennessError()
        {
            var error = ErrorTranslator.Describe(new EngineeringPasswordPolicyViolationException("x"));
            Assert.NotEqual(WireErrorCodes.OpennessError, error.Code);
            Assert.Equal(2, ErrorTranslator.ExitCodeFor(error.Code));
        }
    }

    public class ExitCodeTests
    {
        // This table is documented in the README and is part of the scripting contract.
        [Theory]
        [InlineData(WireErrorCodes.InvalidRequest, 2)]
        [InlineData(WireErrorCodes.NotConnected, 3)]
        [InlineData(WireErrorCodes.NoProjectOpen, 4)]
        [InlineData(WireErrorCodes.NotFound, 5)]
        [InlineData(WireErrorCodes.Ambiguous, 6)]
        [InlineData(WireErrorCodes.AccessDenied, 7)]
        [InlineData(WireErrorCodes.LicenseMissing, 8)]
        [InlineData(WireErrorCodes.PortalUnrecoverable, 9)]
        [InlineData(WireErrorCodes.OpennessError, 1)]
        [InlineData(WireErrorCodes.DaemonError, 1)]
        [InlineData(WireErrorCodes.Internal, 1)]
        [InlineData("something_new", 1)]
        public void ExitCodesMatchTheDocumentedTable(string code, int expected)
        {
            Assert.Equal(expected, ErrorTranslator.ExitCodeFor(code));
        }
    }
}
