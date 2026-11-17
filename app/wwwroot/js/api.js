// Praat met de API. Elk verzoek draagt het access token van Keycloak.
// Wie je bent, bepaalt de API uit dat token; de header X-User bestaat niet meer.

import { getAccessToken, forceRefresh } from './auth.js';

export class ApiError extends Error {
    constructor(status, message) {
        super(message);
        this.status = status;
    }
}

const MESSAGES = {
    400: 'Ongeldige invoer',
    401: 'Niet aangemeld',
    403: 'Geen toegang',
    404: 'Niet gevonden',
    422: 'Onbekende ontvanger: die gebruiker heeft nog nooit aangemeld',
};

async function send(method, path, body, token) {
    const headers = { Authorization: 'Bearer ' + token };
    const options = { method, headers, cache: 'no-store' };
    if (body !== undefined) {
        headers['Content-Type'] = 'application/json';
        options.body = JSON.stringify(body);
    }
    return fetch('/api' + path, options);
}

async function call(method, path, body) {
    let response = await send(method, path, body, await getAccessToken());

    // Token verlopen of ingetrokken: één keer verversen en opnieuw proberen.
    if (response.status === 401) {
        response = await send(method, path, body, await forceRefresh());
    }

    if (!response.ok) {
        let detail = '';
        const type = response.headers.get('Content-Type') || '';
        if (type.includes('json')) {
            const problem = await response.json().catch(() => null);
            detail = problem?.detail || problem?.title || '';
        }
        throw new ApiError(response.status,
            `${MESSAGES[response.status] || 'Fout'} (${method} ${path} gaf ${response.status})` + (detail ? `: ${detail}` : ''));
    }

    if (response.status === 204) return null;
    const type = response.headers.get('Content-Type') || '';
    return type.includes('application/json') ? response.json() : response.text();
}

export const api = {
    me: () => call('GET', '/me'),
    users: () => call('GET', '/users'),
    inbox: (from) => call('GET', '/messages' + (from ? '?from=' + encodeURIComponent(from) : '')),
    search: (q) => call('GET', '/messages/search?q=' + encodeURIComponent(q)),
    message: (id) => call('GET', '/messages/' + encodeURIComponent(id)),
    send: (message) => call('POST', '/messages', message),
    remove: (id) => call('DELETE', '/messages/' + encodeURIComponent(id)),
    exportJson: () => call('GET', '/messages/export'),
};
