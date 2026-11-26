// Hands the authorization code back to the opener on the same origin only (DEC-082). External file: the CSP forbids inline scripts.
const params = new URLSearchParams(location.search);
if (window.opener) window.opener.postMessage({ code: params.get("code"), state: params.get("state") }, location.origin);
