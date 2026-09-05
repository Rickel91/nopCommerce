/*
 * NMI checkout integration for nopCommerce 4.60.
 *
 * Card data is entered into Collect.js hosted fields (iframes served by the gateway). When the shopper continues,
 * Collect.js exchanges the card data for a single-use payment_token, which is posted to the store in a hidden field
 * and forwarded to the Payment API (transact.php). Card numbers never touch the store's page or server.
 */
(function () {
    'use strict';

    if (window.NmiCheckout) {
        window.NmiCheckout.scan();
        return;
    }

    var vendorPromise = null;
    var active = null;

    function setStatus(root, text, isError, isSuccess) {
        var status = root.querySelector('[data-nmi-status]');
        if (!status)
            return;
        status.textContent = text || '';
        status.classList.toggle('message-error', Boolean(isError));
        status.classList.toggle('message-success', Boolean(isSuccess) && !isError);
    }

    function getContinueButton(root) {
        var form = root.closest('form');
        return (form && form.querySelector('.payment-info-next-step-button')) ||
            document.querySelector('#payment-info-buttons-container .payment-info-next-step-button');
    }

    function loadVendor(root) {
        if (window.CollectJS)
            return Promise.resolve();
        if (vendorPromise)
            return vendorPromise;

        vendorPromise = new Promise(function (resolve, reject) {
            if (!root.dataset.collectUrl || !root.dataset.tokenizationKey) {
                reject(new Error('Collect.js is not configured.'));
                return;
            }

            var script = document.createElement('script');
            script.src = root.dataset.collectUrl;
            // Only the tokenization key goes on the script tag. Any other data-* attribute (for example
            // data-variant) makes Collect.js auto-configure itself at load time and fail before it exposes
            // window.CollectJS; the inline variant and field selectors are passed to configure() instead.
            script.setAttribute('data-tokenization-key', root.dataset.tokenizationKey);
            script.onload = function () {
                if (window.CollectJS)
                    resolve();
                else
                    reject(new Error('Collect.js did not initialize.'));
            };
            script.onerror = function () {
                vendorPromise = null;
                reject(new Error('Collect.js could not be loaded.'));
            };
            document.head.appendChild(script);
        });
        return vendorPromise;
    }

    function initialize(root) {
        if (root.dataset.nmiInitialized === 'true')
            return;
        root.dataset.nmiInitialized = 'true';

        var fields = root.querySelector('[data-nmi-fields]');
        var token = root.querySelector('[data-nmi-token]');
        var button = getContinueButton(root);
        var form = root.closest('form');
        var usesNativeSubmit = Boolean(form && button && form.contains(button) && button.type === 'submit');
        var state = { ready: false, tokenizing: false };
        active = state;

        function setButton(enabled) {
            if (button)
                button.disabled = !enabled;
        }

        function fail(text) {
            token.value = '';
            state.tokenizing = false;
            setButton(state.ready);
            setStatus(root, text, true);
            if (window.console && console.warn)
                console.warn('[NMI] Payment step could not continue: ' + text + ' (fields ready: ' + state.ready + ')');
        }

        function resume() {
            // The token is now in the hidden field; continue the checkout without re-entering tokenization.
            if (usesNativeSubmit) {
                if (button.name && !form.querySelector('input[type="hidden"][name="' + button.name + '"]')) {
                    var hidden = document.createElement('input');
                    hidden.type = 'hidden';
                    hidden.name = button.name;
                    hidden.value = button.value || '';
                    form.appendChild(hidden);
                }
                // HTMLFormElement.submit() bypasses submit listeners, including Collect.js's own interceptor.
                HTMLFormElement.prototype.submit.call(form);
            } else if (button) {
                button.click();
            }
        }

        function startTokenization() {
            if (state.tokenizing)
                return;
            if (!state.ready || !window.CollectJS) {
                setStatus(root, root.dataset.incompleteText, true);
                return;
            }

            state.tokenizing = true;
            setButton(false);
            setStatus(root, root.dataset.processingText, false);
            try {
                window.CollectJS.startPaymentRequest();
            } catch (error) {
                fail(root.dataset.failureText);
            }
        }

        function guard(event) {
            if (token.value) {
                setButton(false);
                return;
            }

            event.preventDefault();
            event.stopImmediatePropagation();
            startTokenization();
        }

        setButton(false);
        if (usesNativeSubmit)
            form.addEventListener('submit', guard, true);
        else if (button)
            button.addEventListener('click', guard, true);

        root.nmiReset = function () {
            token.value = '';
            state.tokenizing = false;
            if (window.CollectJS && state.ready) {
                try {
                    window.CollectJS.clearInputs();
                } catch (error) {
                    // Ignore: the hosted fields may already be gone.
                }
            }
            setButton(state.ready);
            setStatus(root, root.dataset.incompleteText, false);
        };

        loadVendor(root).then(function () {
            window.CollectJS.configure({
                variant: 'inline',
                // Inputs live inside gateway iframes, so their look is declared here rather than in the stylesheet.
                styleSniffer: false,
                customCss: {
                    'box-sizing': 'border-box',
                    'height': '44px',
                    'padding': '0 12px',
                    'border': '1px solid #d5d5d5',
                    'border-radius': '4px',
                    'background-color': '#ffffff',
                    'color': '#333333',
                    'font-family': 'Arial, Helvetica, sans-serif',
                    'font-size': '15px',
                    'line-height': '42px',
                    'transition': 'border-color 150ms, box-shadow 150ms'
                },
                placeholderCss: {
                    'color': '#9a9a9a'
                },
                focusCss: {
                    'border-color': '#4ab2f1',
                    'box-shadow': '0 0 0 3px rgba(74, 178, 241, 0.25)',
                    'outline': 'none'
                },
                validCss: {
                    'border-color': '#4cb17c'
                },
                invalidCss: {
                    'border-color': '#e4434b',
                    'color': '#b8322f'
                },
                timeoutDuration: 15000,
                fields: {
                    ccnumber: {
                        selector: '#nmi-ccnumber',
                        title: root.dataset.cardNumberLabel,
                        placeholder: '0000 0000 0000 0000'
                    },
                    ccexp: {
                        selector: '#nmi-ccexp',
                        title: root.dataset.expirationLabel,
                        placeholder: 'MM / YY'
                    },
                    cvv: {
                        selector: '#nmi-cvv',
                        title: root.dataset.securityCodeLabel,
                        placeholder: '***',
                        display: 'required'
                    }
                },
                fieldsAvailableCallback: function () {
                    if (active !== state)
                        return;
                    state.ready = true;
                    fields.setAttribute('aria-busy', 'false');
                    setButton(true);
                    setStatus(root, root.dataset.incompleteText, false);
                },
                validationCallback: function (field, valid, message) {
                    if (active !== state || state.tokenizing)
                        return;
                    setStatus(root, valid ? root.dataset.incompleteText : (message || root.dataset.incompleteText), !valid);
                },
                timeoutCallback: function () {
                    if (active !== state)
                        return;
                    fail(root.dataset.incompleteText);
                },
                callback: function (response) {
                    if (active !== state)
                        return;
                    state.tokenizing = false;
                    if (!response || !response.token) {
                        fail(root.dataset.failureText);
                        return;
                    }

                    token.value = response.token;
                    setStatus(root, root.dataset.readyText, false, true);
                    resume();
                }
            });
        }).catch(function (error) {
            if (window.console && console.error)
                console.error('[NMI] Collect.js could not be initialised from ' + root.dataset.collectUrl + ': ' + (error && error.message ? error.message : error));
            fields.setAttribute('aria-busy', 'false');
            state.ready = false;
            fail(root.dataset.failureText);
            setButton(false);
        });
    }

    function scan() {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', scan, { once: true });
            return;
        }

        document.querySelectorAll('[data-nmi-payment]').forEach(initialize);
    }

    window.NmiCheckout = {
        scan: scan,
        reset: function () {
            document.querySelectorAll('[data-nmi-payment]').forEach(function (root) {
                if (root.nmiReset)
                    root.nmiReset();
            });
        }
    };

    if (window.jQuery) {
        window.jQuery(document).on('accordion_section_opened.nmiCheckout', function (event) {
            if (event.currentSectionId !== 'opc-payment_info')
                return;

            // A token that was already submitted is single-use; require fresh card entry when the step is reopened.
            document.querySelectorAll('[data-nmi-payment]').forEach(function (root) {
                var token = root.querySelector('[data-nmi-token]');
                if (token && token.value && root.nmiReset)
                    root.nmiReset();
            });
        });
    }

    window.addEventListener('pageshow', function (event) {
        if (event.persisted)
            window.NmiCheckout.reset();
    });

    scan();
    new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
})();
