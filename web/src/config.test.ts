import { describe, expect, it } from 'vitest';
import { readConfig } from './config';

describe('readConfig', () => {
  it('reports a missing API address instead of guessing one', () => {
    const c = readConfig({});
    expect(c.apiBaseUrl).toBeNull();
    expect(c.problem).toMatch(/VITE_API_BASE_URL/);
  });

  it('rejects localhost and plain http on a published site', () => {
    expect(readConfig({ VITE_API_BASE_URL: 'http://localhost:5080', PROD: true }).problem).toMatch(/localhost/);
    expect(readConfig({ VITE_API_BASE_URL: 'http://api.example.com', PROD: true }).problem).toMatch(/https/);
  });

  it('allows localhost during development and trims a trailing slash', () => {
    const c = readConfig({ VITE_API_BASE_URL: 'http://localhost:5080/', PROD: false });
    expect(c.problem).toBeNull();
    expect(c.apiBaseUrl).toBe('http://localhost:5080');
  });

  it('accepts an https address in production', () => {
    expect(readConfig({ VITE_API_BASE_URL: 'https://api.example.com', PROD: true }).apiBaseUrl).toBe('https://api.example.com');
  });

  it('rejects text that is not a web address', () => {
    expect(readConfig({ VITE_API_BASE_URL: 'not a url' }).problem).toMatch(/not a valid/);
  });

  it('defaults to demo sign-in and accepts none', () => {
    expect(readConfig({}).authMode).toBe('demo');
    expect(readConfig({ VITE_AUTH_MODE: 'none' }).authMode).toBe('none');
  });
});
