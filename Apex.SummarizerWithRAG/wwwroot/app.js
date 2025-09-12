class ChatbotApp {
    constructor() {
        this.settings = {
            apiEndpoint: '/search'
        };
        this.uploadedFiles = []; // pending files (not yet uploaded)

        // Canonical: server-driven documents keyed by documentId
        this.indexedDocs = new Map();        // docId -> { name, index }

        this.chatHistory = [];
        this.isSending = false; // prevent double send

        this.initializeElements();
        this.initializeEventListeners();
        this.loadSettings();
        this.updateConnectionStatus();

        // Initial fetch from server
        this.refreshIndexedFromServer().finally(() => {
            this.renderFileLists();
        });
    }

    async refreshIndexedFromServer() {
        try {
            const resp = await fetch('/indexed', { headers: { 'Accept': 'application/json' } });
            if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
            const payload = await resp.json();

            // Accept both legacy (array) and new (object with items/Items) shapes
            const list =
                Array.isArray(payload) ? payload
                : Array.isArray(payload.items) ? payload.items
                : Array.isArray(payload.Items) ? payload.Items
                : [];

            if (list.length === 0) {
                return;
            }

            // Clear
            this.indexedDocs.clear();

            // Populate
            for (const it of list) {
                const name = it.FileName || it.fileName || it.SourceName || it.sourceName || '';
                const docId = it.DocumentId || it.documentId || '';
                const index = it.Index || it.index || '';
                if (!name || !docId) continue;

                // Canonical structure
                this.indexedDocs.set(docId, { name, index });
            }
        } catch (e) {
            console.warn('Failed to fetch indexed files:', e);
        }
    }

    initializeElements() {
        // Main elements
        this.chatMessages = document.getElementById('chatMessages');
        this.messageInput = document.getElementById('messageInput');
        this.sendBtn = document.getElementById('sendBtn');
        this.typingIndicator = document.getElementById('typingIndicator');

        // Settings elements
        this.settingsPanel = document.getElementById('settingsPanel');
        this.settingsToggle = document.getElementById('settingsToggle');
        this.modelSelect = document.getElementById('model');
        this.clearChatBtn = document.getElementById('clearChat');
        this.clearLocalFilesBtn = document.getElementById('clearLocalFiles');

        // Hide "Clear Local Files" (no local persistence now)
        if (this.clearLocalFilesBtn) {
            this.clearLocalFilesBtn.style.display = 'none';
        }

        // File upload elements
        this.fileUploadArea = document.getElementById('fileUploadArea');
        this.fileInput = document.getElementById('fileInput');
        this.fileUploadBtn = document.getElementById('fileUploadBtn');
        this.uploadedFilesContainer = document.getElementById('uploadedFiles'); // pending uploads
        this.indexedFilesContainer = document.getElementById('indexedFiles');   // indexed files
        this.uploadTrigger = document.querySelector('.upload-trigger');

        // Status elements
        this.connectionStatus = document.getElementById('connectionStatus');
        this.toastContainer = document.getElementById('toastContainer');
    }

    initializeEventListeners() {
        // Message input and sending
        if (this.messageInput) {
            this.messageInput.addEventListener('input', () => this.handleInputChange());
            this.messageInput.addEventListener('keydown', (e) => this.handleKeyDown(e));
            // Auto-resize textarea
            this.messageInput.addEventListener('input', () => this.autoResizeTextarea());
        }
        if (this.sendBtn) {
            this.sendBtn.addEventListener('click', () => this.sendMessage());
        }

        // Settings
        if (this.settingsToggle) {
            this.settingsToggle.addEventListener('click', () => this.toggleSettings());
        }
        if (this.modelSelect) {
            this.modelSelect.addEventListener('change', () => this.updateSettings());
        }
        if (this.clearChatBtn) {
            this.clearChatBtn.addEventListener('click', () => this.clearChat());
        }

        // File upload
        if (this.fileUploadBtn) {
            this.fileUploadBtn.addEventListener('click', () => this.toggleFileUpload());
        }
        if (this.fileInput) {
            this.fileInput.addEventListener('change', async (e) => await this.handleFileSelect(e));
        }
        if (this.uploadTrigger && this.fileInput) {
            // prevent double opening
            this.uploadTrigger.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                this.fileInput.click();
            });
        }

        // Drag and drop
        if (this.fileUploadArea) {
            this.fileUploadArea.addEventListener('dragover', (e) => this.handleDragOver(e));
            this.fileUploadArea.addEventListener('dragleave', (e) => this.handleDragLeave(e));
            this.fileUploadArea.addEventListener('drop', async (e) => await this.handleFileDrop(e));

            // Only trigger when clicking the area itself, not its children
            this.fileUploadArea.addEventListener('click', (e) => {
                if (e.target === e.currentTarget && this.fileInput) {
                    this.fileInput.click();
                }
            });
        }

        // Close settings on mobile when clicking outside
        document.addEventListener('click', (e) => {
            if (window.innerWidth <= 768) {
                const panel = this.settingsPanel;
                const toggle = this.settingsToggle;
                const target = e.target;
                if (panel && !panel.contains(target) && (!toggle || !toggle.contains(target))) {
                    panel.classList.remove('open');
                }
            }
        });
    }

    loadSettings() {
        this.settings.model = this.modelSelect?.value || this.settings.model || '';
        if (this.modelSelect) {
            this.modelSelect.value = this.settings.model;
        }
    }

    updateSettings() {
        this.settings.model = this.modelSelect.value;
        this.updateConnectionStatus();
    }

    toggleSettings() {
        if (window.innerWidth <= 768) {
            this.settingsPanel.classList.toggle('open');
        }
    }

    updateConnectionStatus() {
        const isValidUrl = this.isValidUrl(this.settings.apiEndpoint);
        this.connectionStatus.className = `status ${isValidUrl ? 'status--success' : 'status--error'}`;
        this.connectionStatus.innerHTML = `
            <div class="status-dot"></div>
            ${isValidUrl ? 'Ready' : 'Invalid URL'}
        `;
    }

    isValidUrl(value) {
        try {
            if (typeof value === 'string' && value.startsWith('/')) return true;
            new URL(value);
            return true;
        } catch {
            return false;
        }
    }

    handleInputChange() {
        const hasText = this.messageInput.value.trim().length > 0;
        this.sendBtn.disabled = !hasText || this.isSending;
    }

    handleKeyDown(e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            if (!this.sendBtn.disabled) {
                this.sendMessage();
            }
        }
    }

    autoResizeTextarea() {
        this.messageInput.style.height = 'auto';
        this.messageInput.style.height = Math.min(this.messageInput.scrollHeight, 120) + 'px';
    }

    toggleFileUpload() {
        this.fileUploadArea.classList.toggle('hidden');
    }

    handleDragOver(e) {
        e.preventDefault();
        this.fileUploadArea.classList.add('dragover');
    }

    handleDragLeave(e) {
        e.preventDefault();
        this.fileUploadArea.classList.remove('dragover');
    }

    async handleFileDrop(e) {
        e.preventDefault();
        this.fileUploadArea.classList.remove('dragover');
        const files = Array.from(e.dataTransfer.files);
        const accepted = this.processFiles(files);
        if (accepted.length > 0) {
            await this.uploadFiles(accepted);
            this.uploadedFiles = [];
            await this.refreshIndexedFromServer();
            this.renderFileLists();
            this.handleInputChange();
        }
    }

    async handleFileSelect(e) {
        const files = Array.from(e.target.files);
        const accepted = this.processFiles(files);
        e.target.value = '';
        if (accepted.length > 0) {
            await this.uploadFiles(accepted);
            this.uploadedFiles = [];
            await this.refreshIndexedFromServer();
            this.renderFileLists();
            this.handleInputChange();
        }
    }

    processFiles(files) {
        const supportedTypes = ['.pdf', '.doc', '.docx', '.txt', '.md', '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.csv', '.xlsx', '.json'];
        const maxSizeMB = 100;
        const maxSize = maxSizeMB * 1024 * 1024;
        const accepted = [];

        files.forEach(file => {
            const fileExt = '.' + file.name.split('.').pop().toLowerCase();

            if (!supportedTypes.includes(fileExt)) {
                this.showToast(`File type ${fileExt} is not supported`, 'error');
                return;
            }

            if (file.size > maxSize) {
                this.showToast(`File ${file.name} is too large (max ${maxSizeMB}MB)`, 'error');
                return;
            }

            const fileId = Date.now() + Math.random();
            const fileData = {
                id: fileId,
                file: file,
                name: file.name,
                size: this.formatFileSize(file.size),
                type: fileExt
            };

            this.uploadedFiles.push(fileData);
            accepted.push(fileData);
        });

        this.renderFileLists();

        if (files.length > 0) {
            this.fileUploadArea.classList.add('hidden');
            this.showToast(`${accepted.length} file(s) added`, 'success');
        }

        return accepted;
    }

    formatFileSize(bytes) {
        if (bytes === 0) return '0 Bytes';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }

    // Wrapper: render both lists
    renderFileLists() {
        this.renderIndexedFiles();
        this.renderPendingFiles();
    }

    // Render indexed (server-fetched) files; list each document by documentId
    renderIndexedFiles() {
        const hasIndexed = this.indexedDocs.size > 0;
        if (!this.indexedFilesContainer) return;

        if (!hasIndexed) {
            this.indexedFilesContainer.classList.add('hidden');
            this.indexedFilesContainer.innerHTML = '';
            return;
        }

        this.indexedFilesContainer.classList.remove('hidden');

        const truncateFileName = (name, maxLen = 27) => {
            if (!name || name.length <= maxLen) return name || '';
            // Preserve extension if present
            const dot = name.lastIndexOf('.');
            if (dot > 0 && dot < name.length - 1) {
                const ext = name.slice(dot); // includes dot
                const base = name.slice(0, dot);
                const keep = Math.max(1, maxLen - ext.length - 1); // leave room for ellipsis
                return base.slice(0, keep) + '…' + ext;
            }
            // No extension
            return name.slice(0, maxLen) + '…';
        };

        const rows = Array.from(this.indexedDocs.entries()).map(([docId, { name, index }]) => {
            const displayName = truncateFileName(name);
            const fullDocId = docId || '';
            return `
                <div class="uploaded-file">
                    <div class="uploaded-file-info">
                        <div class="uploaded-file-icon">
                            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                                <polyline points="14,2 14,8 20,8"/>
                            </svg>
                        </div>
                        <div class="uploaded-file-details">
                            <div class="uploaded-file-name" title="${name}">${displayName}</div>
                            <div class="uploaded-file-index-id">${fullDocId ? fullDocId : ''}</div>
                        </div>
                    </div>
                    <button class="remove-file-btn" title="Delete from memory"
                            data-id="${encodeURIComponent(docId)}"
                            onclick="app.handleDeleteButtonClick(event)">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
                            <line x1="18" y1="6" x2="6" y2="18"/>
                            <line x1="6" y1="6" x2="18" y2="18"/>
                        </svg>
                    </button>
                </div>`;
        }).join('');

        this.indexedFilesContainer.innerHTML = `
            <div class="uploaded-files-section">
                <div class="uploaded-files-title">Indexed files (${this.indexedDocs.size})</div>
                <div class="uploaded-files-list">
                    ${rows}
                </div>
            </div>
        `;
    }

    // Click handler used by inline button HTML
    handleDeleteButtonClick(e) {
        const docId = decodeURIComponent(e.currentTarget.dataset.id || '');
        if (docId) {
            this.deleteIndexedDocumentById(docId);
        }
    }

    // Delete by documentId (unambiguous)
    async deleteIndexedDocumentById(docId) {
        const entry = this.indexedDocs.get(docId);
        if (!entry) {
            this.showToast('Delete unavailable for this entry', 'error');
            console.warn(`[KM] Delete unavailable (missing entry) for docId="${docId}"`);
            return;
        }
        const { name, index } = entry;

        if (!confirm(`Remove "${name}"?\n\nDocumentId: ${docId}`)) return;

        const short = (id) => (id && id.length > 14) ? id.slice(0, 6) + '�' + id.slice(-6) : (id || '');

        try {
            const url = `/memory/${encodeURIComponent(docId)}${index ? `?index=${encodeURIComponent(index)}` : ''}`;
            const resp = await fetch(url, { method: 'DELETE' });

            // 2xx: success
            if (resp.status === 204 || resp.status === 200) {
                // Remove locally on success
                this.indexedDocs.delete(docId);
                this.renderFileLists();

                console.info(`[KM] Successfully deleted "${name}" (docId=${short(docId)}, index=${index || '(auto)'}), status=${resp.status}.`);
                this.showToast(`Removed "${name}"`, 'success');

                await this.refreshIndexedFromServer();
                this.renderFileLists();
                return;
            }

            // 404: could be "not found" OR "not ready" (server message clarifies)
            if (resp.status === 404) {
                const msg = (await resp.text().catch(() => '')) || '';
                // Heuristic: server message includes "not ready" when ingestion not completed
                if (/not ready/i.test(msg)) {
                    console.info(`[KM] "${name}" (docId=${short(docId)}, index=${index || '(auto)'}) not ready yet; server said: ${msg}`);
                    this.showToast(`"${name}" is not ready yet; please retry in a moment.`, 'warn');
                    return; // keep it in UI
                }

                // Otherwise treat as "already removed"
                this.indexedDocs.delete(docId);
                this.renderFileLists();

                console.info(`[KM] "${name}" (docId=${short(docId)}, index=${index || '(auto)'}) was already removed on server; cleaned up locally.`);
                this.showToast(`"${name}" was already removed on the server; UI updated.`, 'info');

                await this.refreshIndexedFromServer();
                this.renderFileLists();
                return;
            }

            // Other errors
            const msg = await resp.text().catch(() => '') || `HTTP ${resp.status}`;
            throw new Error(msg);
        } catch (err) {
            console.error(`[KM] Failed to delete "${name}" (docId=${short(docId)}, index=${entry.index || '(auto)'}):`, err);
            this.showToast(`Failed to delete: ${err.message}`, 'error');
        }
    }

    // Render pending (pre-upload) files
    renderPendingFiles() {
        const hasPending = this.uploadedFiles.length > 0;

        if (!this.uploadedFilesContainer) return;

        if (!hasPending) {
            this.uploadedFilesContainer.classList.add('hidden');
            this.uploadedFilesContainer.innerHTML = '';
            return;
        }

        this.uploadedFilesContainer.classList.remove('hidden');

        this.uploadedFilesContainer.innerHTML = `
            <div class="uploaded-files-section">
                <div class="uploaded-files-title">Pending uploads (${this.uploadedFiles.length})</div>
                ${this.uploadedFiles.map(file => `
                    <div class="uploaded-file" data-file-id="${file.id}">
                        <div class="uploaded-file-info">
                            <div class="uploaded-file-icon">
                                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                                    <polyline points="14,2 14,8 20,8"/>
                                </svg>
                            </div>
                            <div class="uploaded-file-details">
                                <div class="uploaded-file-name" title="${file.name}">${file.name}</div>
                                <div class="uploaded-file-size">${file.size}</div>
                            </div>
                        </div>
                        <button class="remove-file-btn" onclick="app.removeFile(${file.id})" title="Remove from pending">
                            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                <line x1="18" y1="6" x2="6" y2="18"/>
                                <line x1="6" y1="6" x2="18" y2="18"/>
                            </svg>
                        </button>
                    </div>
                `).join('')}
            </div>
        `;
    }

    removeFile(fileId) {
        this.uploadedFiles = this.uploadedFiles.filter(file => file.id !== fileId);
        this.renderFileLists();
        this.handleInputChange();
        this.showToast('File removed', 'info');
    }

    // Upload files to /extract/upload as multipart/form-data for indexing
    async uploadFiles(files) {
        if (!files || files.length === 0) return { success: true };

        const formData = new FormData();
        files.forEach(fd => formData.append('files', fd.file, fd.name));

        try {
            const resp = await fetch('/extract/upload', { method: 'POST', body: formData });
            if (!resp.ok) {
                const txt = await resp.text();
                throw new Error(txt || `HTTP ${resp.status}`);
            }

            // Parse server response and inject items immediately, so they show while transitioning
            const body = await resp.json().catch(() => null);
            const items = (body && (body.Files || body.files)) || [];
            if (Array.isArray(items)) {
                for (const it of items) {
                    const name = it.FileName || it.fileName || '';
                    const docId = it.DocumentId || it.documentId || '';
                    const index = it.Index || it.index || '';
                    if (!name || !docId) continue;

                    // Canonical
                    this.indexedDocs.set(docId, { name, index });
                }
                // Render immediately so users can retract/delete pending docs
                this.renderFileLists();
            }

            // Then fetch authoritative state from the server to reconcile
            await this.refreshIndexedFromServer();

            this.showToast(`Indexed ${files.length} file(s) started`, 'success');
            return { success: true };
        } catch (err) {
            this.showToast(`Indexation failed: ${err.message}`, 'error');
            return { success: false, error: err };
        } finally {
            this.renderFileLists();
        }
    }

    showTypingIndicator() {
        if (this.typingIndicator) {
            this.typingIndicator.classList.remove('hidden');
            this.scrollToBottom();
        }
    }

    hideTypingIndicator() {
        if (this.typingIndicator) {
            this.typingIndicator.classList.add('hidden');
        }
    }

    scrollToBottom() {
        try {
            this.chatMessages.scrollTop = this.chatMessages.scrollHeight;
        } catch { /* ignore */ }
    }

    clearChat() {
        try {
            const messages = this.chatMessages?.querySelectorAll('.message');
            messages?.forEach(m => m.remove());
            this.chatHistory = [];
            this.showToast('Chat cleared', 'success');
        } catch (e) {
            this.showToast(`Failed to clear chat: ${e.message}`, 'error');
        }
    }

    async sendMessage() {
        const message = this.messageInput.value.trim();
        if (!message || this.isSending) return; // guard

        // lock UI immediately
        this.isSending = true;
        this.sendBtn.disabled = true;
        this.sendBtn.setAttribute('aria-busy', 'true');

        this.addMessage(message, 'user');
        this.messageInput.value = '';
        this.autoResizeTextarea();

        this.showTypingIndicator();

        try {
            const params = { query: message };
            const selectedModel = (this.modelSelect?.value || '').trim();
            if (selectedModel) params.model = selectedModel;

            const url = `${this.settings.apiEndpoint}?${new URLSearchParams(params).toString()}`;
            const response = await fetch(url, { method: 'GET', headers: { 'Accept': 'application/json' } });

            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }

            const data = await response.json();

            this.hideTypingIndicator();
            const citations = data.Citations || data.citations || [];
            this.addMessage(data.Answer || data.answer || data.response || data.message || 'No response received', 'assistant', citations);
        } catch (error) {
            this.hideTypingIndicator();
            this.addMessage(`Error: ${error.message}. Please check your API endpoint and try again.`, 'assistant');
            this.showToast('Failed to send message', 'error');
            this.updateConnectionStatus();
        } finally {
            // unlock; re-evaluate enablement based on input text
            this.isSending = false;
            this.sendBtn.removeAttribute('aria-busy');
            this.handleInputChange();
        }
    }

    // Append a chat message (unchanged, except id->name mapping uses indexedDocs)
    addMessage(content, role = 'assistant', citations = null) {
        if (!this.chatMessages) return;

        const isUser = role === 'user';
        const wrapper = document.createElement('div');
        wrapper.className = `message message--${isUser ? 'user' : 'assistant'}`;

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';

        try {
            if (typeof marked !== 'undefined' && typeof DOMPurify !== 'undefined') {
                const html = DOMPurify.sanitize(marked.parse(content ?? ''), { USE_PROFILES: { html: true } });
                contentDiv.innerHTML = html;
            } else {
                contentDiv.textContent = content ?? '';
            }
        } catch {
            contentDiv.textContent = content ?? '';
        }

        wrapper.appendChild(contentDiv);

        // Render references/citations under assistant messages
        const refs = Array.isArray(citations) ? citations : [];
        if (!isUser && refs.length > 0) {
            // Build id->name map from canonical store
            const idToName = new Map();
            for (const [docId, { name }] of this.indexedDocs.entries()) {
                idToName.set(docId, name);
            }

            const refsDiv = document.createElement('div');
            refsDiv.className = 'message-citations';

            const list = document.createElement('div');
            list.className = 'message-citations__list';

            const truncate = (t, len = 220) => {
                if (!t) return '';
                const s = t.trim().replace(/\s+/g, ' ');
                return s.length > len ? s.slice(0, len) + '…' : s;
            };

            refs.forEach((r) => {
                const item = document.createElement('div');
                item.className = 'message-citation';

                const resolvedName = r.SourceName || idToName.get(r.DocumentId || '') || '';
                const shortId = (r.DocumentId && r.DocumentId.length > 14)
                    ? r.DocumentId.slice(0, 6) + '…' + r.DocumentId.slice(-6)
                    : (r.DocumentId || '');
                const titleText = resolvedName || shortId || r.Link || '';

                if (titleText) {
                    const title = document.createElement('div');
                    title.className = 'message-citation__title';
                    title.textContent = titleText;
                    item.appendChild(title);
                }

                const meta = document.createElement('div');
                meta.className = 'message-citation__meta';
                const metaBits = [];
                if (r.Index) metaBits.push(r.Index);
                if (!resolvedName && shortId) metaBits.push(shortId);
                if (r.SourceContentType) metaBits.push(r.SourceContentType.toUpperCase());
                if (metaBits.length > 0) {
                    meta.textContent = metaBits.join(' • ');
                    item.appendChild(meta);
                }

                const parts = Array.isArray(r.Partitions) ? r.Partitions : [];
                if (parts.length > 0) {
                    parts.forEach((p) => {
                        const pDiv = document.createElement('div');
                        pDiv.className = 'message-citation__snippet';
                        const page = Number.isFinite(p.SectionNumber) && p.SectionNumber > 0 ? `p.${p.SectionNumber} ` : '';
                        const rel = (p.Relevance ?? 0).toFixed ? ` (${(p.Relevance).toFixed(3)})` : '';
                        pDiv.textContent = `${page}${truncate(p.Text || '')}${rel}`;
                        item.appendChild(pDiv);
                    });
                }
                if (item.childNodes.length > 0) {
                    list.appendChild(item);
                }
            });

            refsDiv.appendChild(list);
            wrapper.appendChild(refsDiv);
        }

        this.chatMessages.appendChild(wrapper);
        this.chatHistory.push({ role, content, citations: refs, timestamp: new Date().toISOString() });
        this.scrollToBottom();
    }

    showToast(message, type = 'info') {
        const container = this.toastContainer || document.getElementById('toastContainer');
        if (!container) {
            const log = type === 'error' ? console.error : (type === 'warn' ? console.warn : console.log);
            log(message);
            return;
        }

        const toast = document.createElement('div');
        toast.className = `toast toast--${type}`;
        toast.innerHTML = `
            <div class="toast-message">${message}</div>
            <button class="toast-close" aria-label="Close" onclick="this.parentElement.remove()">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <line x1="18" y1="6" x2="6" y2="18"></line>
                    <line x1="6" y1="6" x2="18" y2="18"></line>
                </svg>
            </button>
        `;
        container.appendChild(toast);
        setTimeout(() => toast.remove(), 5000);
    }
}

document.addEventListener('DOMContentLoaded', () => {
    window.app = new ChatbotApp();
});
