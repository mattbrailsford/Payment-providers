using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Inline
{
    [PaymentProvider("StripeSubscriptionInline")]
    public class StripeSubscription : APaymentProvider
    {
        public override string DocumentationLink { get { return "https://stripe.com/docs"; } }

        public override bool SupportsRetrievalOfPaymentStatus { get { return false; } }
        public override bool SupportsCapturingOfPayment { get { return false; } }
        public override bool SupportsRefundOfPayment { get { return false; } }
        public override bool SupportsCancellationOfPayment { get { return false; } }
        public override bool FinalizeAtContinueUrl { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string> {
                    { "form_url", "" },
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    //{ "validate_cvc", "false" },
                    //{ "validate_address", "false" },
                    //{ "address_property_alias", "streetAddress" },
                    //{ "validate_zipcode", "false" },
                    //{ "zipcode_property_alias", "zipCode" },
                    //{ "validate_country", "false" },
                    { "test_secret_key", "" },
                    { "test_public_key", "" },
                    { "live_secret_key", "" },
                    { "live_public_key", "" },
                    { "mode", "test" },
                };
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("form_url", "settings");
            settings.MustContainKey("mode", "settings");
            settings.MustContainKey(settings["mode"] + "_public_key", "settings");

            var htmlForm = new PaymentHtmlForm
            {
                Action = settings["form_url"]
            };

            var settingsToExclude = new[] { "form_url", "capture", "test_secret_key", "test_public_key", "live_secret_key", "live_public_key", "mode" };
            htmlForm.InputFields = settings.Where(i => !settingsToExclude.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value);

            htmlForm.InputFields["api_key"] = settings[settings["mode"] + "_public_key"];
            htmlForm.InputFields["continue_url"] = teaCommerceContinueUrl;
            htmlForm.InputFields["cancel_url"] = teaCommerceCancelUrl;

            if (settings.ContainsKey("address_property_alias") && !string.IsNullOrWhiteSpace(settings["address_property_alias"]))
                htmlForm.InputFields["address"] = order.Properties.First(x => x.Alias == settings["address_property_alias"]).Value;

            if (settings.ContainsKey("zipcode_property_alias") && !string.IsNullOrWhiteSpace(settings["zipcode_property_alias"]))
                htmlForm.InputFields["zipcode"] = order.Properties.First(x => x.Alias == settings["zipcode_property_alias"]).Value;

            if (order.PaymentInformation != null && order.PaymentInformation.CountryId > 0)
            {
                var country = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId);
                htmlForm.InputFields["country"] = country.RegionCode.ToLowerInvariant();
            }

            return htmlForm;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("continue_url", "settings");

            return settings["continue_url"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("cancel_url", "settings");

            return settings["cancel_url"];
        }

        public override string GetCartNumber(HttpRequest request, IDictionary<string, string> settings)
        {
            string cartNumber = "";

            try
            {
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest(request, logPostData: true);
                }

                // See if this is from a stripe event
                var stripeEvent = GetStripeEvent(request);
                if (stripeEvent != null)
                {
                    // If it's an invoice, see if it comes from a subscription with a cartNumber recorded against it
                    if (stripeEvent.Type.StartsWith("invoice."))
                    {
                        var invoice = (StripeInvoice)Mapper<StripeInvoice>.MapFromJson(stripeEvent.Data.Object.ToString());
                        var lineItem = invoice.StripeInvoiceLineItems.Data.FirstOrDefault(x => x.Type == "subscription" && x.Metadata.ContainsKey("cartNumber"));
                        if (lineItem != null)
                        {
                            cartNumber = lineItem.Metadata["cartNumber"];
                        }
                    }
                }
                else
                {
                    HttpContext.Current.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                }

            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe Subscription - Get cart number");
            }

            return cartNumber;
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest(request, logPostData: true);
                }

                // Get the current stripe api key based on mode
                var stripeApiKey = settings[settings["mode"] + "_secret_key"];

                // Get the Plan ID (Assumes a subscription order is a single order line with the sku being the planId)
                var orderLine = order.OrderLines.First();
                var planId = orderLine.Properties["planId"];
                if (string.IsNullOrWhiteSpace(planId))
                    planId = orderLine.Sku;

                // Create customer and subscribe to plan
                var customerService = new StripeCustomerService(stripeApiKey);
                var customer = customerService.Create(new StripeCustomerCreateOptions
                {
                    Email = order.PaymentInformation.Email,
                    SourceToken = request.Form["stripeToken"]
                });

                var subscriptionService = new StripeSubscriptionService(stripeApiKey);
                var subscription = subscriptionService.Create(customer.Id, planId, new StripeSubscriptionCreateOptions
                {
                    TaxPercent = order.PaymentInformation.VatRate.Value * 100,
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderId", order.Id.ToString() },
                        { "cartNumber", order.CartNumber }
                    }
                });

                // Stash the stripe info in the order
                order.Properties.AddOrUpdate("stripeCustomerId", customer.Id);
                order.Properties.AddOrUpdate("stripeSubscriptionId", subscription.Id);
                order.Save();

                // Authorize the payment. We'll capture it on a successful webhook callback
                callbackInfo = new CallbackInfo((decimal)subscription.StripePlan.Amount / 100, subscription.Id, PaymentState.Authorized);
            }
            catch (StripeException e)
            {

                // Pass through request fields
                var requestFields = string.Join("", request.Form.AllKeys.Select(k => "<input type=\"hidden\" name=\"" + k + "\" value=\"" + request.Form[k] + "\" />"));

                //Add error details from the exception.
                requestFields = requestFields + "<input type=\"hidden\" name=\"TransactionFailed\" value=\"true\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.chargeId\" value=\"" + e.StripeError.ChargeId + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Code\" value=\"" + e.StripeError.Code + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Error\" value=\"" + e.StripeError.Error + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.ErrorSubscription\" value=\"" + e.StripeError.ErrorSubscription + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.ErrorType\" value=\"" + e.StripeError.ErrorType + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Message\" value=\"" + e.StripeError.Message + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Parameter\" value=\"" + e.StripeError.Parameter + "\" />";

                var paymentForm = PaymentMethodService.Instance.Get(order.StoreId, order.PaymentInformation.PaymentMethodId.Value).GeneratePaymentForm(order, requestFields);

                //Force the form to auto submit
                paymentForm += "<script type=\"text/javascript\">document.forms[0].submit();</script>";

                //Write out the form
                HttpContext.Current.Response.Clear();
                HttpContext.Current.Response.Write(paymentForm);
                HttpContext.Current.Response.End();
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe Subscription (" + order.CartNumber + ") - ProcessCallback");
            }

            return callbackInfo;
        }

        public override string ProcessRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                
                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest(request, logPostData: true);
                } 

                // Get the current stripe event
                var stripeEvent = GetStripeEvent(request);

                // With subscriptions, Stripe creates an invoice for each payment
                // so to ensure subscription is live, we'll listen for successful invoice payment
                if(stripeEvent.Type.StartsWith("invoice"))
                {
                    var invoice = (StripeInvoice)Mapper<StripeInvoice>.MapFromJson(stripeEvent.Data.Object.ToString());

                    if (stripeEvent.Type == "invoice.payment_succeeded" 
                        && order.TransactionInformation.PaymentState != PaymentState.Captured)
                    {
                        order.TransactionInformation.TransactionId = invoice.ChargeId;
                        order.TransactionInformation.PaymentState = PaymentState.Captured;
                        order.Save();
                    }
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe Subscription (" + order.CartNumber + ") - ProcessRequest");
                throw;
            }

            return "";
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "form_url":
                    return settingsKey + "<br/><small>The url of the page with the Stripe payment form on - e.g. /payment/</small>";
                case "continue_url":
                    return settingsKey + "<br/><small>The url to navigate to after payment is processed - e.g. /confirmation/</small>";
                case "cancel_url":
                    return settingsKey + "<br/><small>The url to navigate to if the customer cancels the payment - e.g. /cancel/</small>";
                //case "validate_cvc":
                //    return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards cvc number - true/false.</small>";
                //case "validate_address":
                //    return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards address matches the billing address - true/false.</small>";
                //case "address_property_alias":
                //    return settingsKey + "<br/><small>The alias of the field containing the billing address - e.g. billingStreetAddress.</small>";
                //case "validate_zipcode":
                //    return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards zip code matches the billing zip code - true/false.</small>";
                //case "zipcode_property_alias":
                //    return settingsKey + "<br/><small>The alias of the field containing the billing zip code - e.g. billingZipCode.</small>";
                //case "validate_country":
                //    return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards country matches the billing country - true/false.</small>";
                case "test_secret_key":
                    return settingsKey + "<br/><small>Your test stripe secret key.</small>";
                case "test_public_key":
                    return settingsKey + "<br/><small>Your test stripe public key.</small>";
                case "live_secret_key":
                    return settingsKey + "<br/><small>Your live stripe secret key.</small>";
                case "live_public_key":
                    return settingsKey + "<br/><small>Your live stripe public key.</small>";
                case "mode":
                    return settingsKey + "<br/><small>The mode of the provider - test/live.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected StripeEvent GetStripeEvent(HttpRequest request)
        {
            StripeEvent stripeEvent = null;

            if (HttpContext.Current.Items["TC_StripeEvent"] != null)
            {
                stripeEvent = (StripeEvent)HttpContext.Current.Items["TC_StripeEvent"];
            }
            else
            {
                try
                {
                    if (request.InputStream.CanSeek)
                    {
                        request.InputStream.Seek(0, SeekOrigin.Begin);
                    }

                    using (var reader = new StreamReader(request.InputStream))
                    {
                        stripeEvent = StripeEventUtility.ParseEvent(reader.ReadToEnd());

                        HttpContext.Current.Items["TC_StripeEvent"] = stripeEvent;
                    }
                }
                catch
                { }
            }

            return stripeEvent;
        }
    }
}
