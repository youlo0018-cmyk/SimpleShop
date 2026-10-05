import { defineStore } from 'pinia';
import { request } from '@/core/http';
import {
  clearSession,
  getProfile,
  getToken,
  setProfile,
  setToken,
} from '@/core/session-storage';

export interface CustomerProfile {
  customerId: string;
  customerName: string;
  nickName: string;
  avatar: string;
}

interface LoginResult {
  customerId: string;
  token: string;
  customerName: string;
  nickName: string;
  avatar: string;
}

interface RegisterResult {
  customerId: string;
  token: string;
  customerName: string;
  nickName: string;
}

export const useSessionStore = defineStore('customer-session', {
  state: () => ({
    token: '',
    profile: null as CustomerProfile | null,
  }),
  getters: {
    loggedIn: (state) => Boolean(state.token),
    displayName: (state) => state.profile?.nickName || state.profile?.customerName || '游客',
  },
  actions: {
    restore() {
      this.token = getToken();
      this.profile = getProfile<CustomerProfile>();
    },
    async login(customerName: string, password: string) {
      const result = await request<LoginResult>('/gateway/customers/Login', {
        method: 'POST',
        auth: false,
        data: { customerName, password },
      });
      this.token = result.token;
      this.profile = {
        customerId: result.customerId,
        customerName: result.customerName,
        nickName: result.nickName,
        avatar: result.avatar,
      };
      setToken(result.token);
      setProfile(this.profile);
    },
    async register(customerName: string, password: string, phone: string, nickName: string) {
      const result = await request<RegisterResult>('/gateway/customers/Register', {
        method: 'POST',
        auth: false,
        data: { customerName, password, phone, nickName },
      });
      this.token = result.token;
      this.profile = {
        customerId: result.customerId,
        customerName: result.customerName,
        nickName: result.nickName,
        avatar: '',
      };
      setToken(result.token);
      setProfile(this.profile);
    },
    logout() {
      this.token = '';
      this.profile = null;
      clearSession();
    },
  },
});
