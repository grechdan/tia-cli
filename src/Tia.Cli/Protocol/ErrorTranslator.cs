using System;

namespace TiaCli.Protocol
{
    /// <summary>
    /// Turns arbitrary .NET and Openness exceptions into stable <see cref="WireError"/>s.
    /// Everything here matches on type *names* rather than types, so this file never references the
    /// Siemens assembly and is safe to touch before the resolver has run.
    /// </summary>
    public static class ErrorTranslator
    {
        public static WireError Describe(Exception ex)
        {
            // Openness wraps the real cause; reporting the wrapper alone is useless to the caller.
            while (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                ex = aggregate.InnerExceptions[0];

            if (ex is WireException wire)
                return new WireError
                {
                    Code = wire.Code,
                    Message = wire.Message,
                    Hint = wire.Hint,
                    Detail = wire.Detail,
                };

            // SessionException lives in the Openness namespace and cannot be referenced from here
            // without dragging Siemens types in, so it is matched structurally instead.
            var codeProperty = ex.GetType().GetProperty("Code");
            var hintProperty = ex.GetType().GetProperty("Hint");
            if (ex.GetType().Name == "SessionException" && codeProperty != null)
            {
                return new WireError
                {
                    Code = codeProperty.GetValue(ex) as string,
                    Message = ex.Message,
                    Hint = hintProperty?.GetValue(ex) as string,
                };
            }

            var typeName = ex.GetType().FullName ?? string.Empty;
            var detail = ex.InnerException?.Message;

            // Openness reports a missing license by wrapping LicenseNotFoundException inside a
            // TargetInvocation-style exception, so the cause is only visible down the inner chain.
            for (var cause = ex; cause != null; cause = cause.InnerException)
            {
                var causeType = cause.GetType().FullName ?? string.Empty;
                if (!causeType.EndsWith("LicenseNotFoundException", StringComparison.Ordinal)) continue;

                return new WireError
                {
                    Code = WireErrorCodes.LicenseMissing,
                    Message = cause.Message,
                    Detail = cause.Message == ex.Message ? null : ex.Message,
                    Hint = "This operation needs a licensed TIA Portal product (typically STEP 7 " +
                           "Professional). Reading projects works without it; creating or changing " +
                           "hardware does not.",
                };
            }

            if (typeName.EndsWith("EngineeringSecurityException", StringComparison.Ordinal))
            {
                return new WireError
                {
                    Code = WireErrorCodes.AccessDenied,
                    Message = ex.Message,
                    Detail = detail,
                    Hint = "Add your Windows account to the local 'Siemens TIA Openness' group and " +
                           "sign out and back in. If you are already a member, TIA Portal is asking " +
                           "you to confirm this program in its own window - answer that dialog once " +
                           "and the confirmation is remembered until tia.exe is rebuilt.",
                };
            }

            if (typeName.EndsWith("NonRecoverableException", StringComparison.Ordinal))
            {
                return new WireError
                {
                    Code = WireErrorCodes.PortalUnrecoverable,
                    Message = ex.Message,
                    Detail = detail,
                    Hint = "The TIA Portal instance is no longer usable. Run 'tia session stop' and start again.",
                };
            }

            // A disposed portal handle is not a usage error the caller can fix by changing arguments;
            // the instance is gone and the only way forward is a fresh session.
            if (typeName.EndsWith("EngineeringObjectDisposedException", StringComparison.Ordinal) ||
                ex is ObjectDisposedException)
            {
                return new WireError
                {
                    Code = WireErrorCodes.PortalUnrecoverable,
                    Message = ex.Message,
                    Detail = detail,
                    Hint = "The TIA Portal instance is gone. Run 'tia session stop', then start again.",
                };
            }

            if (typeName.StartsWith("Siemens.Engineering", StringComparison.Ordinal))
                return new WireError { Code = WireErrorCodes.OpennessError, Message = ex.Message, Detail = detail };

            return new WireError
            {
                Code = WireErrorCodes.Internal,
                Message = ex.Message,
                Detail = typeName,
            };
        }

        /// <summary>Process exit code for an error code. 0 is success, 1 is a generic failure.</summary>
        public static int ExitCodeFor(string code)
        {
            switch (code)
            {
                case WireErrorCodes.InvalidRequest: return 2;
                case WireErrorCodes.NotConnected: return 3;
                case WireErrorCodes.NoProjectOpen: return 4;
                case WireErrorCodes.NotFound: return 5;
                case WireErrorCodes.Ambiguous: return 6;
                case WireErrorCodes.AccessDenied: return 7;
                case WireErrorCodes.LicenseMissing: return 8;
                case WireErrorCodes.PortalUnrecoverable: return 9;
                default: return 1;
            }
        }
    }
}
