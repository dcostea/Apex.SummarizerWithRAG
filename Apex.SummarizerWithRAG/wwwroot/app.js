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

        // Track active citations popup
        this.activeCitationsPopup = null;

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
                const ext = name.slice(dot);
                const base = name.slice(0, dot);
                const keep = Math.max(1, maxLen - ext.length - 1);
                return base.slice(0, keep) + '…' + ext;
            }
            return name.slice(0, maxLen) + '…';
        };

        const rows = Array.from(this.indexedDocs.entries()).map(([docId, { name }]) => {
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

        // Always show, even if empty (for smoother UX)
        if (this.indexedFilesContainer.classList.contains('hidden')) {
            this.indexedFilesContainer.classList.remove('hidden');
        }

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

        const short = (id) => (id && id.length > 14) ? id.slice(0, 6) + '…' + id.slice(-6) : (id || '');

        try {
            const url = `/memory/${encodeURIComponent(docId)}${index ? `?index=${encodeURIComponent(index)}` : ''}`;
            const resp = await fetch(url, { method: 'DELETE' });

            if (resp.status === 204 || resp.status === 200) {
                this.indexedDocs.delete(docId);
                this.renderFileLists();

                console.info(`[KM] Successfully deleted "${name}" (docId=${short(docId)}, index=${index || '(auto)'}), status=${resp.status}.`);
                this.showToast(`Removed "${name}"`, 'success');

                await this.refreshIndexedFromServer();
                this.renderFileLists();
                return;
            }

            if (resp.status === 404) {
                const msg = (await resp.text().catch(() => '')) || '';
                if (/not ready/i.test(msg)) {
                    console.info(`[KM] "${name}" (docId=${short(docId)}, index=${index || '(auto)'}) not ready yet; server said: ${msg}`);
                    this.showToast(`"${name}" is not ready yet; please retry in a moment.`, 'warn');
                    return;
                }

                this.indexedDocs.delete(docId);
                this.renderFileLists();

                console.info(`[KM] "${name}" (docId=${short(docId)}, index=${index || '(auto)'}) was already removed on server; cleaned up locally.`);
                this.showToast(`"${name}" was already removed on the server; UI updated.`, 'info');

                await this.refreshIndexedFromServer();
                this.renderFileLists();
                return;
            }

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

        // Before sending, close any open citations popup
        this.closeCitationsPopup();

        // capture selected model at send time
        const selectedModel = (this.modelSelect?.value || '').trim();

        // add user message with meta
        this.addMessage(message, 'user', null, {
            model: selectedModel || 'default',
            timestamp: new Date().toISOString()
        });
        this.messageInput.value = '';
        this.autoResizeTextarea();

        this.showTypingIndicator();

        try {
            const params = { query: message };
            if (selectedModel) params.model = selectedModel;

            const url = `${this.settings.apiEndpoint}?${new URLSearchParams(params).toString()}`;
            const response = await fetch(url, { method: 'GET', headers: { 'Accept': 'application/json' } });

            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }

            const data = await response.json();

            this.hideTypingIndicator();
            const citations = data.Citations || data.citations || [];
            const modelFromServer = data.Model || data.model || selectedModel || 'default';
            this.addMessage(
                data.Answer || data.answer || data.response || data.message || 'No response received',
                'assistant',
                citations,
                {
                    model: modelFromServer,
                    timestamp: new Date().toISOString()
                }
            );
        } catch (error) {
            this.hideTypingIndicator();
            const selectedOrDefault = selectedModel || 'default';
            this.addMessage(`Error: ${error.message}. Please check your API endpoint and try again.`, 'assistant', null, {
                model: selectedOrDefault,
                timestamp: new Date().toISOString()
            });
            this.showToast('Failed to send message', 'error');
            this.updateConnectionStatus();
        } finally {
            // unlock; re-evaluate enablement based on input text
            this.isSending = false;
            this.sendBtn.removeAttribute('aria-busy');
            this.handleInputChange();
        }
    }

    // Build KernelMemory doc/chunk refs from citations (case-tolerant)
    buildKmRefs(citations) {
        const docs = new Map(); // docId -> { index?: string, count: number }
        const chunkRefs = [];   // { documentId, index, partition, section, relevance }

        (citations || []).forEach(c => {
            const docId = c.DocumentId || c.documentId || '';
            const index = c.Index || c.index || '';
            const parts = Array.isArray(c.Partitions) ? c.Partitions
                        : Array.isArray(c.partitions) ? c.partitions
                        : [];

            if (docId) {
                if (!docs.has(docId)) docs.set(docId, { index, count: 0 });
                const entry = docs.get(docId);
                parts.forEach(p => {
                    const partition = p.PartitionNumber ?? p.partitionNumber;
                    const section = p.SectionNumber ?? p.sectionNumber;
                    const relevance = p.Relevance ?? p.relevance;
                    entry.count += 1;
                    chunkRefs.push({ documentId: docId, index, partition, section, relevance });
                });
            } else {
                parts.forEach(p => {
                    const partition = p.PartitionNumber ?? p.partitionNumber;
                    const section = p.SectionNumber ?? p.sectionNumber;
                    const relevance = p.Relevance ?? p.relevance;
                    chunkRefs.push({ documentId: '', index, partition, section, relevance });
                });
            }
        });

        const docIds = Array.from(docs.keys());
        const chunkCount = chunkRefs.length;

        return {
            docIds,
            chunkCount,
            chunks: chunkRefs,
            indexByDoc: Object.fromEntries(
                Array.from(docs.entries()).map(([id, { index }]) => [id, index])
            )
        };
    }

    // Append a chat message with optional citations and meta (model + timestamp + KM refs)
    addMessage(content, role = 'assistant', citations = null, meta = null) {
        if (!this.chatMessages) return;

        const isUser = role === 'user';
        const wrapper = document.createElement('div');
        wrapper.className = `message message--${isUser ? 'user' : 'assistant'}`;

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';

        // Render content (markdown -> sanitized HTML when available)
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

        // Footer meta (timestamp + model [+ refs])
        const footerDiv = document.createElement('div');
        footerDiv.className = 'message-meta';

        const tsSpan = document.createElement('span');
        tsSpan.className = 'message-meta__item';
        const ts = meta?.timestamp || new Date().toISOString();
        tsSpan.textContent = this.formatTimestamp(ts);

        const sep1 = document.createElement('span');
        sep1.className = 'message-meta__sep';
        sep1.textContent = '•';

        const modelSpan = document.createElement('span');
        modelSpan.className = 'message-meta__item';
        const modelValue = (meta?.model || (isUser ? (this.modelSelect?.value || 'default') : 'default')).toString();
        modelSpan.textContent = `Model: ${modelValue}`;

        footerDiv.appendChild(tsSpan);
        footerDiv.appendChild(sep1);
        footerDiv.appendChild(modelSpan);

        // If assistant, summarize KernelMemory refs
        let km = null;
        let refsDivForPopup = null;
        if (!isUser) {
            const refs = Array.isArray(citations) ? citations : [];
            km = this.buildKmRefs(refs);

            const sep2 = document.createElement('span');
            sep2.className = 'message-meta__sep';
            sep2.textContent = '•';

            const refsSpan = document.createElement('span');
            refsSpan.className = 'message-meta__item message-meta__link';
            refsSpan.setAttribute('role', 'button');
            refsSpan.setAttribute('tabindex', '0');
            refsSpan.title = 'View citations';

            const shortId = (id) => {
                if (!id) return '';
                return id.length > 14 ? `${id.slice(0, 6)}…${id.slice(-6)}` : id;
            };

            if (km.docIds.length === 1) {
                const only = km.docIds[0];
                refsSpan.textContent = `Doc: ${shortId(only)} • ${km.chunkCount} chunk${km.chunkCount === 1 ? '' : 's'}`;
            } else {
                refsSpan.textContent = `Refs: ${km.docIds.length} doc${km.docIds.length === 1 ? '' : 's'} • ${km.chunkCount} chunk${km.chunkCount === 1 ? '' : 's'}`;
            }

            footerDiv.appendChild(sep2);
            footerDiv.appendChild(refsSpan);

            // Build citations element (detached, for popup)
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

                const docId = r.DocumentId || r.documentId || '';
                const resolvedName = r.SourceName || r.sourceName || idToName.get(docId || '') || '';
                const shortDocId = (docId && docId.length > 14) ? docId.slice(0, 6) + '…' + docId.slice(-6) : (docId || '');
                const titleText = resolvedName || shortDocId || r.Link || r.link || r.SourceUrl || r.sourceUrl || '';

                if (titleText) {
                    const title = document.createElement('div');
                    title.className = 'message-citation__title';
                    title.textContent = titleText;
                    item.appendChild(title);
                }

                // Normalize partitions first so we can show their numbers in meta
                const parts = Array.isArray(r.Partitions) ? r.Partitions
                            : Array.isArray(r.partitions) ? r.partitions
                            : [];

                // Meta line: index • docId (if no name) • TYPE • chunks #n,#m,...
                const metaDiv = document.createElement('div');
                metaDiv.className = 'message-citation__meta';
                const metaBits = [];
                const idx = r.Index || r.index;
                if (idx) metaBits.push(idx);
                if (!resolvedName && docId) metaBits.push(docId);
                if (docId) metaBits.push(docId);

                const partNums = parts
                    .map(p => p.PartitionNumber ?? p.partitionNumber)
                    .filter(n => Number.isFinite(n))
                    .map(n => `#${n}`)
                    .join(', ');
                //if (partNums) metaBits.push(`chunks ${partNums}`);

                if (metaBits.length > 0) {
                    metaDiv.textContent = metaBits.join(' • ');
                    item.appendChild(metaDiv);
                }

                // Each snippet: chunk #<partition> • p.<section> <text> (rel)
                parts.forEach((p) => {
                    const pDiv = document.createElement('div');
                    pDiv.className = 'message-citation__snippet';
                    const partitionNumber = p.PartitionNumber ?? p.partitionNumber;
                    const sectionNumber = p.SectionNumber ?? p.sectionNumber;
                    const relevance = p.Relevance ?? p.relevance;
                    const chunk = Number.isFinite(partitionNumber) ? `chunk #${partitionNumber} • ` : '';
                    const page = Number.isFinite(sectionNumber) && sectionNumber > 0 ? `p.${sectionNumber} ` : '';
                    const rel = (typeof relevance === 'number' && isFinite(relevance)) ? ` • (${relevance.toFixed(3)})` : '';
                    const txt = p.Text ?? p.text ?? '';
                    //pDiv.textContent = `${chunk}${page}${truncate(txt)}${rel}`;
                    pDiv.textContent = `${chunk}${page}${txt}${rel}`;
                    item.appendChild(pDiv);
                });

                if (item.childNodes.length > 0) {
                    list.appendChild(item);
                }
            });

            refsDiv.appendChild(list);
            refsDivForPopup = refsDiv;

            // Toggle popup on click/keyboard
            const openHandler = (ev) => {
                ev.preventDefault();
                ev.stopPropagation();
                if (refsDivForPopup) {
                    this.toggleCitationsPopup(refsSpan, refsDivForPopup);
                }
            };
            refsSpan.addEventListener('click', openHandler);
            refsSpan.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                    openHandler(e);
                }
            });

            // Expose for programmatic access
            wrapper.dataset.kmDocIds = km.docIds.join(',');
            wrapper.dataset.kmChunkCount = String(km.chunkCount);
        }

        // Place footer inside the bubble at the bottom
        contentDiv.appendChild(footerDiv);

        wrapper.appendChild(contentDiv);

        this.chatMessages.appendChild(wrapper);
        this.chatHistory.push({
            role,
            content,
            citations: Array.isArray(citations) ? citations : [],
            timestamp: ts,
            model: modelValue,
            km // { docIds, chunkCount, chunks, indexByDoc }
        });
        this.scrollToBottom();
    }

    // Popup helpers
    toggleCitationsPopup(anchorEl, refsContentEl) {
        if (this.activeCitationsPopup?.anchor === anchorEl) {
            this.closeCitationsPopup();
            return;
        }
        this.showCitationsPopup(anchorEl, refsContentEl);
    }

    showCitationsPopup(anchorEl, refsContentEl) {
        this.closeCitationsPopup();

        // Overlay
        const overlay = document.createElement('div');
        overlay.className = 'popup-overlay';
        overlay.addEventListener('click', () => this.closeCitationsPopup());

        // Popup container
        const popup = document.createElement('div');
        popup.className = 'citations-popup';
        popup.setAttribute('role', 'dialog');
        popup.setAttribute('aria-label', 'Citations');

        // Stop events inside popup from bubbling to overlay/window
        ['click', 'wheel', 'touchstart', 'touchmove'].forEach(evt =>
            popup.addEventListener(evt, (e) => e.stopPropagation(), { passive: true })
        );

        // Header
        const header = document.createElement('div');
        header.className = 'citations-popup__header';
        const title = document.createElement('div');
        title.className = 'citations-popup__title';
        title.textContent = 'Citations';
        const closeBtn = document.createElement('button');
        closeBtn.className = 'citations-popup__close';
        closeBtn.type = 'button';
        closeBtn.title = 'Close';
        closeBtn.innerHTML = '&times;';
        closeBtn.addEventListener('click', () => this.closeCitationsPopup());
        header.appendChild(title);
        header.appendChild(closeBtn);

        // Content (clone so we don't mutate any existing nodes)
        const body = document.createElement('div');
        body.className = 'citations-popup__body';
        body.appendChild(refsContentEl.cloneNode(true));

        popup.appendChild(header);
        popup.appendChild(body);

        document.body.appendChild(overlay);
        document.body.appendChild(popup);

        // Initial position
        this.positionCitationsPopup(anchorEl, popup);

        const onKeyDown = (e) => {
            if (e.key === 'Escape') this.closeCitationsPopup();
        };
        document.addEventListener('keydown', onKeyDown, true);

        // Reposition on resize (do NOT close on scroll so user can scroll inside popup)
        const onResize = () => this.positionCitationsPopup(anchorEl, popup);
        window.addEventListener('resize', onResize, true);

        this.activeCitationsPopup = {
            anchor: anchorEl,
            overlay,
            popup,
            onKeyDown,
            onResize
        };
    }

    positionCitationsPopup(anchorEl, popup) {
        if (!anchorEl || !popup) return;

        // Prepare for measurement
        popup.style.visibility = 'hidden';
        popup.style.top = '0px';
        popup.style.left = '0px';

        const rect = anchorEl.getBoundingClientRect();
        const margin = 8;

        // Ensure width fits viewport
        const maxWidth = Math.min(560, window.innerWidth - margin * 2);
        popup.style.maxWidth = `${maxWidth}px`;

        // Force layout to get dimensions
        const desiredTop = rect.bottom + margin;
        const desiredLeft = Math.min(
            Math.max(margin, rect.left),
            window.innerWidth - popup.offsetWidth - margin
        );

        let top = desiredTop;
        // If bottom overflows, place above the anchor
        if (top + popup.offsetHeight + margin > window.innerHeight) {
            top = Math.max(margin, rect.top - popup.offsetHeight - margin);
        }

        popup.style.left = `${desiredLeft}px`;
        popup.style.top = `${top}px`;
        popup.style.visibility = 'visible';
    }

    closeCitationsPopup() {
        const active = this.activeCitationsPopup;
        if (!active) return;
        try {
            active.overlay?.remove();
            active.popup?.remove();
            document.removeEventListener('keydown', active.onKeyDown, true);
            if (active.onResize) window.removeEventListener('resize', active.onResize, true);
        } finally {
            this.activeCitationsPopup = null;
        }
    }

    formatTimestamp(value) {
        try {
            const d = value instanceof Date ? value : new Date(value);
            return d.toLocaleString(undefined, { hour12: false });
        } catch {
            return new Date().toLocaleString(undefined, { hour12: false });
        }
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
