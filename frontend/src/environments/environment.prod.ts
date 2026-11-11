// Use the co-hosted nginx API proxy in production without baking a backend hostname into JS.
export const environment = { apiBaseUrl: '/api' } as const;
