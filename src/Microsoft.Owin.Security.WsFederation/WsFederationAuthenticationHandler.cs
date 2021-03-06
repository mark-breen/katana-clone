﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security.Infrastructure;
using Microsoft.Owin.Security.Notifications;

namespace Microsoft.Owin.Security.WsFederation
{
    public class WsFederationAuthenticationHandler : AuthenticationHandler<WsFederationAuthenticationOptions>
    {
        private readonly ILogger _logger;

        public WsFederationAuthenticationHandler(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Handles Signout
        /// </summary>
        /// <returns></returns>
        protected override async Task ApplyResponseGrantAsync()
        {
            AuthenticationResponseRevoke signout = Helper.LookupSignOut(Options.AuthenticationType, Options.AuthenticationMode);
            if (signout != null)
            {
                object obj = null;
                Context.Environment.TryGetValue("wsfed.SignOutRedirect", out obj);
                string wreply = obj as string;

                WsFederationMessage wsFederationMessage = new WsFederationMessage()
                {
                    IssuerAddress = Options.IssuerAddress ?? string.Empty,
                    Wtrealm = Options.Wtrealm,
                };

                if (!string.IsNullOrWhiteSpace(wreply))
                {
                    wsFederationMessage.Wreply = wreply;
                }
                else if (!string.IsNullOrWhiteSpace(Options.Wreply))
                {
                    wsFederationMessage.Wreply = Options.Wreply;
                }

                if (Options.Notifications != null && Options.Notifications.RedirectToIdentityProvider != null)
                {
                    RedirectToIdentityProviderNotification<WsFederationMessage> notification =
                        new RedirectToIdentityProviderNotification<WsFederationMessage> { ProtocolMessage = wsFederationMessage };
                    await Options.Notifications.RedirectToIdentityProvider(notification);
                    if (notification.Cancel)
                    {
                        return;
                    }
                }

                string redirect = wsFederationMessage.CreateSignOutQueryString();
                if (!Uri.IsWellFormedUriString(redirect, UriKind.Absolute))
                {
                    _logger.WriteError(string.Format(CultureInfo.InvariantCulture, "The WsFederation sign-out redirect uri is not well formed: '{0}'", redirect));
                    return;
                }

                Response.Redirect(redirect);
            }
        }

        /// <summary>
        /// Handles Challenge
        /// </summary>
        /// <returns></returns>
        protected override async Task ApplyResponseChallengeAsync()
        {
            if (Response.StatusCode == 401)
            {
                AuthenticationResponseChallenge challenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);
                if (challenge == null)
                {
                    return;
                }

                string baseUri =
                        Request.Scheme +
                        Uri.SchemeDelimiter +
                        Request.Host +
                        Request.PathBase;

                string currentUri =
                    baseUri +
                    Request.Path +
                    Request.QueryString;

                // Save the original challenge URI so we can redirect back to it when we're done.
                AuthenticationProperties properties = challenge.Properties;
                if (string.IsNullOrEmpty(properties.RedirectUri))
                {
                    properties.RedirectUri = currentUri;
                }

                WsFederationMessage wsFederationMessage = new WsFederationMessage()
                {
                    IssuerAddress = Options.IssuerAddress ?? string.Empty,
                    Wtrealm = Options.Wtrealm,
                    Wctx = WsFederationAuthenticationDefaults.WctxKey + "=" + Uri.EscapeDataString(Options.StateDataFormat.Protect(properties)),
                };
                if (!string.IsNullOrWhiteSpace(Options.Wreply))
                {
                    wsFederationMessage.Wreply = Options.Wreply;
                }

                if (Options.Notifications != null && Options.Notifications.RedirectToIdentityProvider != null)
                {
                    RedirectToIdentityProviderNotification<WsFederationMessage> notification =
                        new RedirectToIdentityProviderNotification<WsFederationMessage> { ProtocolMessage = wsFederationMessage };
                    await Options.Notifications.RedirectToIdentityProvider(notification);
                    if (notification.Cancel)
                    {
                        return;
                    }
                }

                string redirect = wsFederationMessage.CreateSignInQueryString();
                if (!Uri.IsWellFormedUriString(redirect, UriKind.Absolute))
                {
                    _logger.WriteError(string.Format(CultureInfo.InvariantCulture, "The WsFederation sign-in redirect uri is not well formed: '{0}'", redirect));
                    return;
                }

                Response.Redirect(redirect);
            }

            return;
        }

        public override Task<bool> InvokeAsync()
        {
            return InvokeReplyPathAsync();
        }

        // Returns true if the request was handled, false if the next middleware should be invoked.
        private async Task<bool> InvokeReplyPathAsync()
        {
            AuthenticationTicket ticket = await AuthenticateAsync();

            // Redirect back to the original secured resource, if any.
            if (ticket != null && !string.IsNullOrWhiteSpace(ticket.Properties.RedirectUri))
            {
                Response.Redirect(ticket.Properties.RedirectUri);
                return true;
            }

            return false;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            // Allow login to be constrained to a specific path.
            if (Options.CallbackPath.HasValue && Options.CallbackPath != Request.Path)
            {
                return null;
            }

            // assumption: if the ContentType is "application/x-www-form-urlencoded" it should be safe to read as it is small.
            if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(Request.ContentType)
              // May have media/type; charset=utf-8, allow partial match.
              && Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              && Request.Body.CanRead)
            {
                if (!Request.Body.CanSeek)
                {
                    // Buffer in case this body was not meant for us.
                    MemoryStream memoryStream = new MemoryStream();
                    await Request.Body.CopyToAsync(memoryStream);
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    Request.Body = memoryStream;
                }
                IFormCollection form = await Request.ReadFormAsync();
    
                // Post preview release: a delegate on WsFederationAuthenticationOptions would allow for users to hook their own custom message.
                WsFederationMessage wsFederationMessage = new WsFederationMessage(form);
                if (!wsFederationMessage.IsSignInMessage)
                {
                    Request.Body.Seek(0, SeekOrigin.Begin);
                    return null;
                }

                MessageReceivedNotification<WsFederationMessage> messageReceivedNotification = null;
                if (Options.Notifications != null && Options.Notifications.MessageReceived != null)
                {
                    messageReceivedNotification = new MessageReceivedNotification<WsFederationMessage> { ProtocolMessage = wsFederationMessage };
                    await Options.Notifications.MessageReceived(messageReceivedNotification);
                }

                if (messageReceivedNotification != null && messageReceivedNotification.Cancel)
                {
                    return null;
                }

                if (wsFederationMessage.Wresult != null)
                {
                    string token = wsFederationMessage.GetToken();
                    if (Options.Notifications != null && Options.Notifications.SecurityTokenReceived != null)
                    {
                        SecurityTokenReceivedNotification securityTokenReceivedNotification = new SecurityTokenReceivedNotification { SecurityToken = token };
                        await Options.Notifications.SecurityTokenReceived(securityTokenReceivedNotification);
                        if (securityTokenReceivedNotification.Cancel)
                        {
                            return null;
                        }
                    }

                    ExceptionDispatchInfo authFailedEx = null;
                    try
                    {
                        ClaimsPrincipal principal = Options.SecurityTokenHandlers.ValidateToken(token, Options.TokenValidationParameters);
                        ClaimsIdentity claimsIdentity = principal.Identity as ClaimsIdentity;

                        // Retrieve our cached redirect uri
                        string state = wsFederationMessage.Wctx;
                        AuthenticationProperties properties = GetPropertiesFromWctx(state);

                        AuthenticationTicket ticket = new AuthenticationTicket(claimsIdentity, properties);

                        Request.Context.Authentication.SignIn(claimsIdentity);
                        if (Options.Notifications != null && Options.Notifications.SecurityTokenValidated != null)
                        {
                            await Options.Notifications.SecurityTokenValidated(new SecurityTokenValidatedNotification { AuthenticationTicket = ticket });
                        }

                        return ticket;
                    }
                    catch (Exception exception)
                    {
                        // We can't await inside a catch block, capture and handle outside.
                        authFailedEx = ExceptionDispatchInfo.Capture(exception);
                    }

                    if (authFailedEx != null)
                    {
                        if (Options.Notifications != null && Options.Notifications.AuthenticationFailed != null)
                        {
                            // Post preview release: user can update metadata, need consistent messaging.
                            var authenticationFailedNotification = new AuthenticationFailedNotification<WsFederationMessage>()
                            {
                                ProtocolMessage = wsFederationMessage,
                                Exception = authFailedEx.SourceException
                            };

                            await Options.Notifications.AuthenticationFailed(authenticationFailedNotification);

                            if (!authenticationFailedNotification.Cancel)
                            {
                                authFailedEx.Throw();
                            }
                        }
                        else
                        {
                            authFailedEx.Throw();
                        }
                    }
                }
            }

            return null;
        }

        private AuthenticationProperties GetPropertiesFromWctx(string state)
        {
            AuthenticationProperties properties = null;
            if (!string.IsNullOrEmpty(state))
            {
                var pairs = ParseDelimited(state);
                List<string> values;
                if (pairs.TryGetValue(WsFederationAuthenticationDefaults.WctxKey, out values) && values.Count > 0)
                {
                    string value = values.First();
                    properties = Options.StateDataFormat.Unprotect(value);
                }
            }
            return properties;
        }

        private static IDictionary<string, List<string>> ParseDelimited(string text)
        {
            char[] delimiters = new[] { '&', ';' };
            var accumulator = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int textLength = text.Length;
            int equalIndex = text.IndexOf('=');
            if (equalIndex == -1)
            {
                equalIndex = textLength;
            }
            int scanIndex = 0;
            while (scanIndex < textLength)
            {
                int delimiterIndex = text.IndexOfAny(delimiters, scanIndex);
                if (delimiterIndex == -1)
                {
                    delimiterIndex = textLength;
                }
                if (equalIndex < delimiterIndex)
                {
                    while (scanIndex != equalIndex && char.IsWhiteSpace(text[scanIndex]))
                    {
                        ++scanIndex;
                    }
                    string name = text.Substring(scanIndex, equalIndex - scanIndex);
                    string value = text.Substring(equalIndex + 1, delimiterIndex - equalIndex - 1);

                    name = Uri.UnescapeDataString(name.Replace('+', ' '));
                    value = Uri.UnescapeDataString(value.Replace('+', ' '));

                    List<string> existing;
                    if (!accumulator.TryGetValue(name, out existing))
                    {
                        accumulator.Add(name, new List<string>(1) { value });
                    }
                    else
                    {
                        existing.Add(value);
                    }

                    equalIndex = text.IndexOf('=', delimiterIndex);
                    if (equalIndex == -1)
                    {
                        equalIndex = textLength;
                    }
                }
                scanIndex = delimiterIndex + 1;
            }
            return accumulator;
        }
    }
}