/** Describe only public API fields; password hashes never cross this boundary. */
export interface UserRecord { id: string; username: string; role: 'Admin' | 'User'; is_active: boolean; address: string | null; city: string | null; country: string | null; education: string | null; created_at: string; updated_at: string; }
export interface LoginResponse { access_token: string; token_type: 'Bearer'; expires_at: string; user: UserRecord; }
export interface ApiProblem { status: number; code: string; title: string; detail: string; traceId: string; errors?: Record<string, string[]>; }
export interface CreateUserRequest { username: string; password: string; address?: string; city?: string; country?: string; education?: string; }
