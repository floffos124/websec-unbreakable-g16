// Inloggen met OpenID Connect: authorization code flow met PKCE (S256).
//
// - De SPA is een publieke client: ze heeft geen client secret. PKCE bewijst dat
//   wie de code inwisselt, ook wie de login startte.
// - state beschermt tegen CSRF op de callback, nonce tegen het opnieuw afspelen
//   van een ID token.
// - Tokens staan enkel in het geheugen (geen localStorage), zodat ze niet
//   blijven liggen in de browser. Na herladen haalt de SPA via de Keycloak
//   sessie (SSO cookie) zonder wachtwoord nieuwe tokens.

const ISSUER = window.location.origin + '/auth/realms/unbreakable';
const CLIENT_ID = 'unbreakable-spa';
const REDIRECT_URI = window.location.origin + '/';
const SCOPE = 'openid profile';
const TX_KEY = 'unbreakable_oidc_tx';
const TX_MAX_AGE_MS = 10 * 60 * 1000;
const REFRESH_MARGIN_MS = 30 * 1000;

let discovery = null;
let tokens = null;          // { accessToken, refreshToken, idToken, expiresAt, claims }
let refreshing = null;      // lopende refresh (zodat parallelle calls er één delen)

// ------------------------------------------------------------------ hulpfuncties

export function base64UrlEncode(bytes) {
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function base64UrlDecodeToString(text) {
    const padded = text.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((text.length + 3) % 4);
    const binary = atob(padded);
    const bytes = Uint8Array.from(binary, c => c.charCodeAt(0));
    return new TextDecoder().decode(bytes);
}

export function randomString(byteLength = 32) {
    const bytes = new Uint8Array(byteLength);
    crypto.getRandomValues(bytes); // CSPRNG, niet Math.random
    return base64UrlEncode(bytes);
}

export async function pkceChallenge(verifier) {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    return base64UrlEncode(new Uint8Array(digest));
}

// Leest de payload van een JWT. GEEN validatie van de handtekening: de SPA
// gebruikt dit enkel voor de nonce en de weergave. De API valideert zelf.
export function decodeJwtPayload(jwt) {
    const parts = String(jwt || '').split('.');
    if (parts.length !== 3) throw new Error('Ongeldig token formaat');
    return JSON.parse(base64UrlDecodeToString(parts[1]));
}

function sameOrigin(url) {
    try {
        return new URL(url).origin === window.location.origin;
    } catch {
        return false;
    }
}

async function getDiscovery() {
    if (discovery) return discovery;
    const response = await fetch(ISSUER + '/.well-known/openid-configuration', { cache: 'no-store' });
    if (!response.ok) throw new Error('Identity provider niet bereikbaar (' + response.status + ')');
    const doc = await response.json();

    // Enkel endpoints van onze eigen origin aanvaarden.
    if (doc.issuer !== ISSUER
        || !sameOrigin(doc.authorization_endpoint)
        || !sameOrigin(doc.token_endpoint)
        || (doc.end_session_endpoint && !sameOrigin(doc.end_session_endpoint))) {
        throw new Error('Onverwachte configuratie van de identity provider');
    }
    discovery = doc;
    return discovery;
}

function saveTransaction(tx) {
    sessionStorage.setItem(TX_KEY, JSON.stringify(tx));
}

// Eenmalig: lezen en meteen wissen, zodat een callback niet twee keer werkt.
function takeTransaction() {
    const raw = sessionStorage.getItem(TX_KEY);
    sessionStorage.removeItem(TX_KEY);
    if (!raw) return null;
    try {
        const tx = JSON.parse(raw);
        if (!tx || typeof tx !== 'object' || Date.now() - tx.createdAt > TX_MAX_AGE_MS) return null;
        return tx;
    } catch {
        return null;
    }
}

function storeTokens(response) {
    if (!response.access_token || response.token_type?.toLowerCase() !== 'bearer') {
        throw new Error('Onverwacht antwoord van de identity provider');
    }
    const expiresIn = Number(response.expires_in) > 0 ? Number(response.expires_in) : 60;
    const previous = tokens;
    tokens = {
        accessToken: response.access_token,
        // Keycloak stuurt bij een refresh een nieuw refresh token (rotatie).
        refreshToken: response.refresh_token || previous?.refreshToken || null,
        idToken: response.id_token || previous?.idToken || null,
        expiresAt: Date.now() + expiresIn * 1000,
        claims: decodeJwtPayload(response.access_token),
    };
}

async function tokenRequest(params) {
    const { token_endpoint } = await getDiscovery();
    const response = await fetch(token_endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams(params),
        cache: 'no-store',
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
        throw new Error('Token aanvraag mislukt: ' + (data.error || response.status));
    }
    return data;
}

// ------------------------------------------------------------------ flow

// Stap 1: naar Keycloak met code_challenge, state en nonce.
export async function login() {
    const { authorization_endpoint } = await getDiscovery();
    const verifier = randomString(32);           // 43 tekens, minimum volgens RFC 7636
    const state = randomString(16);
    const nonce = randomString(16);

    saveTransaction({
        state,
        nonce,
        verifier,
        returnHash: window.location.hash || '#/inbox',
        createdAt: Date.now(),
    });

    const url = new URL(authorization_endpoint);
    url.search = new URLSearchParams({
        response_type: 'code',
        client_id: CLIENT_ID,
        redirect_uri: REDIRECT_URI,
        scope: SCOPE,
        state,
        nonce,
        code_challenge: await pkceChallenge(verifier),
        code_challenge_method: 'S256',
    }).toString();

    window.location.assign(url.toString());
    // De pagina navigeert weg; nooit verder gaan alsof we ingelogd zijn.
    return new Promise(() => {});
}

// Stap 2: terug van Keycloak met ?code=...&state=...
async function handleCallback(params) {
    const tx = takeTransaction();
    // De code meteen uit de adresbalk halen (niet in history of Referer laten staan).
    window.history.replaceState(null, '', REDIRECT_URI + (tx?.returnHash || '#/inbox'));

    if (params.has('error')) {
        throw new Error('Aanmelden mislukt: ' + params.get('error'));
    }
    if (!tx || params.get('state') !== tx.state) {
        throw new Error('Ongeldige of verlopen aanmeldpoging (state). Probeer opnieuw.');
    }

    const response = await tokenRequest({
        grant_type: 'authorization_code',
        code: params.get('code'),
        redirect_uri: REDIRECT_URI,
        client_id: CLIENT_ID,
        code_verifier: tx.verifier,
    });

    // Controle van het ID token (zonder handtekening, zie decodeJwtPayload).
    const idClaims = decodeJwtPayload(response.id_token);
    const audience = Array.isArray(idClaims.aud) ? idClaims.aud : [idClaims.aud];
    if (idClaims.nonce !== tx.nonce || idClaims.iss !== ISSUER || !audience.includes(CLIENT_ID)) {
        throw new Error('ID token hoort niet bij deze aanmelding');
    }

    storeTokens(response);
}

async function refresh() {
    if (!tokens?.refreshToken) throw new Error('Geen refresh token');
    const response = await tokenRequest({
        grant_type: 'refresh_token',
        refresh_token: tokens.refreshToken,
        client_id: CLIENT_ID,
    });
    storeTokens(response);
}

// Bij het laden van de pagina: callback verwerken of (stil) aanmelden.
export async function init() {
    const params = new URLSearchParams(window.location.search);
    if (params.has('code') || params.has('error')) {
        await handleCallback(params);
        return;
    }
    if (!tokens) {
        await login();
    }
}

// Geeft een geldig access token; ververst het kort voor het verloopt.
export async function getAccessToken() {
    if (!tokens) {
        await login();
    }
    if (Date.now() < tokens.expiresAt - REFRESH_MARGIN_MS) {
        return tokens.accessToken;
    }
    if (!refreshing) {
        refreshing = refresh().finally(() => { refreshing = null; });
    }
    try {
        await refreshing;
        return tokens.accessToken;
    } catch {
        tokens = null;
        await login();
    }
}

// Na een 401 van de API: token is ingetrokken of ongeldig. Eén keer verversen.
export async function forceRefresh() {
    if (tokens) tokens.expiresAt = 0;
    return getAccessToken();
}

export function currentUsername() {
    return tokens?.claims?.preferred_username || '';
}

export async function logout() {
    const idToken = tokens?.idToken;
    tokens = null;
    sessionStorage.removeItem(TX_KEY);
    const { end_session_endpoint } = await getDiscovery();
    const url = new URL(end_session_endpoint);
    url.search = new URLSearchParams({
        client_id: CLIENT_ID,
        post_logout_redirect_uri: REDIRECT_URI,
        ...(idToken ? { id_token_hint: idToken } : {}),
    }).toString();
    window.location.assign(url.toString());
}
