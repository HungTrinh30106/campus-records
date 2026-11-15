// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { MemoryRouter } from 'react-router-dom';
import StudentIdentity from './StudentIdentity';

afterEach(cleanup);
describe('student data rendered in actual list identity component', () => {
  it('displays attacker HTML as literal name/email text and creates no injected nodes', () => {
    const name = '<img src=x onerror="alert(1)">';
    const email = '<script>window.stolen=true</script>';
    const { container } = render(<MemoryRouter><StudentIdentity id="507f1f77bcf86cd799439011" fullName={name} email={email} /></MemoryRouter>);
    expect(screen.getByText(name)).toBeVisible(); expect(screen.getByText(email)).toBeVisible();
    expect(container.querySelector('img,script,iframe')).toBeNull();
    expect(screen.getByRole('link')).toHaveAttribute('href', '/students/507f1f77bcf86cd799439011');
  });
  it('retains already-masked data without requesting or reconstructing the original', () => {
    render(<MemoryRouter><StudentIdentity id="507f1f77bcf86cd799439011" fullName="Nguyễn Minh Anh" email="***@***" /></MemoryRouter>);
    expect(screen.getByText('***@***')).toBeVisible();
    expect(screen.getByText('MA')).toBeVisible();
  });
});
