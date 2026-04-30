const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
const BUILTIN_ROLES = ['Admin', 'Manager', 'Agent'];
const state = { editingId: null };

function showToast(message, type = 'info') {
    const stack = document.getElementById('toastStack');
    if (!stack) return;
    const t = document.createElement('div');
    t.className = `toast toast-${type}`;
    t.textContent = message;
    stack.appendChild(t);
    setTimeout(() => { t.classList.add('toast-out'); setTimeout(() => t.remove(), 200); }, 2400);
}

function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
}

/* ─── Render role cards ───────────────────────────────────────────── */
async function loadRoles() {
    const list = document.getElementById('rolesList');
    list.innerHTML = '<div class="contacts-empty">Loading…</div>';

    try {
        const resp = await fetch('/api/roles');
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        const items = await resp.json();
        renderRoles(items);
    } catch (err) {
        list.innerHTML = `<div class="contacts-empty">Failed to load: ${escapeHtml(err.message)}</div>`;
    }
}

function renderRoles(items) {
    const list = document.getElementById('rolesList');
    if (!items.length) {
        list.innerHTML = '<div class="contacts-empty">No roles defined.</div>';
        return;
    }

    list.innerHTML = items.map(r => {
        const isBuiltin = BUILTIN_ROLES.includes(r.name);
        const perms = r.permissions.length
            ? r.permissions.map(p => `<span class="role-perm-chip">${escapeHtml(p)}</span>`).join('')
            : '<span class="cell-muted">No permissions assigned</span>';

        return `
        <div class="role-card" data-id="${r.id}">
            <div class="role-card-head">
                <h3>${escapeHtml(r.name)}</h3>
                ${isBuiltin ? '<span class="role-card-builtin">Built-in</span>' : ''}
            </div>
            <div class="role-card-meta"><strong>${r.userCount}</strong> user${r.userCount === 1 ? '' : 's'} · <strong>${r.permissions.length}</strong> permission${r.permissions.length === 1 ? '' : 's'}</div>
            <div class="role-perms">${perms}</div>
            <div class="role-card-actions">
                <button class="btn-ghost" type="button" data-action="edit" data-id="${r.id}">Edit</button>
                ${isBuiltin ? '' : `<button class="btn-ghost btn-danger" type="button" data-action="delete" data-id="${r.id}" data-name="${escapeHtml(r.name)}">Delete</button>`}
            </div>
        </div>`;
    }).join('');
}

/* ─── Modal — create / edit ───────────────────────────────────────── */
function openCreateRole() {
    state.editingId = null;
    document.getElementById('roleModalTitle').textContent = 'Add role';
    document.getElementById('roleName').value = '';
    document.getElementById('roleName').disabled = false;
    document.querySelectorAll('input[data-permission]').forEach(cb => cb.checked = false);
    clearRoleError();
    document.getElementById('roleModal').classList.remove('d-none');
    setTimeout(() => document.getElementById('roleName').focus(), 60);
}

async function openEditRole(id) {
    state.editingId = id;
    clearRoleError();
    document.getElementById('roleModal').classList.remove('d-none');
    document.getElementById('roleModalTitle').textContent = 'Edit role';

    try {
        const resp = await fetch(`/api/roles/${encodeURIComponent(id)}`);
        if (!resp.ok) throw new Error('HTTP ' + resp.status);
        const r = await resp.json();

        document.getElementById('roleName').value = r.name;
        // Built-in roles can be edited (permissions adjusted) but their name is locked.
        document.getElementById('roleName').disabled = BUILTIN_ROLES.includes(r.name);

        const perms = new Set(r.permissions);
        document.querySelectorAll('input[data-permission]').forEach(cb => {
            cb.checked = perms.has(cb.dataset.permission);
        });
    } catch (err) {
        showToast('Failed to load role: ' + err.message, 'error');
        closeRoleModal();
    }
}

function closeRoleModal() {
    document.getElementById('roleModal').classList.add('d-none');
    state.editingId = null;
}
window.closeRoleModal = closeRoleModal;

function showRoleError(msg) {
    const el = document.getElementById('roleFormError');
    el.textContent = msg;
    el.classList.remove('d-none');
}
function clearRoleError() {
    document.getElementById('roleFormError').classList.add('d-none');
}

document.getElementById('roleSaveBtn').addEventListener('click', async () => {
    const btn = document.getElementById('roleSaveBtn');
    const spinner = btn.querySelector('.btn-spinner');
    const labelEl = btn.querySelector('.btn-label');

    const name = document.getElementById('roleName').value.trim();
    if (!name) { showRoleError('Role name is required.'); return; }

    const checked = Array.from(document.querySelectorAll('input[data-permission]:checked'))
        .map(cb => cb.dataset.permission);

    btn.disabled = true;
    spinner.classList.remove('d-none');
    labelEl.textContent = 'Saving…';

    try {
        const resp = await fetch('/api/roles', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
            body: JSON.stringify({ id: state.editingId, name, permissions: checked })
        });
        if (!resp.ok) {
            const err = await resp.json().catch(() => ({}));
            showRoleError(err.error || 'Failed to save role.');
            return;
        }
        showToast(state.editingId ? 'Role updated' : 'Role created', 'success');
        closeRoleModal();
        loadRoles();
    } catch (err) {
        showRoleError('Network error: ' + err.message);
    } finally {
        btn.disabled = false;
        spinner.classList.add('d-none');
        labelEl.textContent = 'Save';
    }
});

/* "Toggle all" inside a permission group */
document.addEventListener('click', (e) => {
    const link = e.target.closest('button[data-toggle-group]');
    if (!link) return;
    const group = link.dataset.toggleGroup;
    const checks = document.querySelectorAll(`input[data-permission][data-group="${group}"]`);
    const allChecked = Array.from(checks).every(cb => cb.checked);
    checks.forEach(cb => cb.checked = !allChecked);
});

/* ─── Card actions ────────────────────────────────────────────────── */
document.addEventListener('click', async (e) => {
    const btn = e.target.closest('button[data-action]');
    if (!btn) return;
    const id = btn.dataset.id;

    if (btn.dataset.action === 'edit') return openEditRole(id);

    if (btn.dataset.action === 'delete') {
        const name = btn.dataset.name || 'this role';
        openConfirm({
            title: 'Delete role?',
            body: `<strong>${escapeHtml(name)}</strong> will be permanently removed.`,
            confirmLabel: 'Delete',
            isDanger: true,
            onConfirm: async () => {
                const resp = await fetch(`/api/roles/${encodeURIComponent(id)}`, {
                    method: 'DELETE',
                    headers: { 'RequestVerificationToken': token() }
                });
                if (!resp.ok) {
                    const err = await resp.json().catch(() => ({}));
                    showToast(err.error || 'Failed.', 'error');
                    return;
                }
                showToast('Role deleted', 'success');
                loadRoles();
            }
        });
    }
});

/* ─── Confirm helper ──────────────────────────────────────────────── */
let pendingConfirm = null;

function openConfirm({ title, body, confirmLabel, isDanger, onConfirm }) {
    pendingConfirm = onConfirm;
    document.getElementById('confirmTitle').textContent = title;
    document.getElementById('confirmBody').innerHTML = body || '';
    const btn = document.getElementById('confirmAction');
    btn.textContent = confirmLabel || 'Confirm';
    btn.className = 'btn-ghost ' + (isDanger ? 'btn-danger' : '');
    document.getElementById('confirmModal').classList.remove('d-none');
}

function closeConfirmModal() {
    pendingConfirm = null;
    document.getElementById('confirmModal').classList.add('d-none');
}
window.closeConfirmModal = closeConfirmModal;

document.getElementById('confirmAction')?.addEventListener('click', async () => {
    const fn = pendingConfirm;
    closeConfirmModal();
    if (typeof fn === 'function') await fn();
});

/* ─── Boot ────────────────────────────────────────────────────────── */
document.getElementById('addRoleBtn').addEventListener('click', openCreateRole);

loadRoles();
