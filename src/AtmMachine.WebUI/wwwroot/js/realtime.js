(function () {
    const body = document.body;
    if (!body || body.dataset.authenticated !== "true" || !window.signalR) {
        return;
    }

    const statusNode = document.getElementById("realtime-status");
    const liveFeed = document.getElementById("live-feed");
    const adminFeed = document.getElementById("admin-live-audit-feed");
    const currency = new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD"
    });

    function setStatus(text, extraClass) {
        if (!statusNode) {
            return;
        }

        statusNode.textContent = text;
        statusNode.classList.remove("status-live", "status-warn", "status-offline");
        if (extraClass) {
            statusNode.classList.add(extraClass);
        }
    }

    function formatMoney(value) {
        const amount = Number(value);
        if (!Number.isFinite(amount)) {
            return "$0.00";
        }

        return currency.format(amount);
    }

    function showToast(kind, title, message) {
        if (!liveFeed) {
            return;
        }

        const toast = document.createElement("section");
        toast.className = "live-toast" + (kind ? " " + kind : "");

        const heading = document.createElement("strong");
        heading.textContent = title;
        toast.appendChild(heading);

        const text = document.createElement("p");
        text.textContent = message;
        toast.appendChild(text);

        liveFeed.prepend(toast);
        while (liveFeed.children.length > 5) {
            liveFeed.removeChild(liveFeed.lastElementChild);
        }

        window.setTimeout(() => {
            toast.classList.add("closing");
            window.setTimeout(() => toast.remove(), 450);
        }, 5200);
    }

    function updateBalances(payload) {
        const accountId = String(payload.accountId || "").toLowerCase();
        document.querySelectorAll("[data-account-available='" + accountId + "']").forEach((node) => {
            node.textContent = formatMoney(payload.availableBalance);
        });
        document.querySelectorAll("[data-account-ledger='" + accountId + "']").forEach((node) => {
            node.textContent = formatMoney(payload.ledgerBalance);
        });

        if (payload.totalBalance !== undefined) {
            document.querySelectorAll("[data-total-balance]").forEach((node) => {
                node.textContent = formatMoney(payload.totalBalance);
            });
        }
    }

    function appendAdminAudit(payload) {
        if (!adminFeed) {
            return;
        }

        if (adminFeed.firstElementChild && adminFeed.firstElementChild.classList.contains("muted")) {
            adminFeed.innerHTML = "";
        }

        const item = document.createElement("li");
        const actor = payload.actorUserId ? payload.actorUserId.slice(0, 8) : "system";
        item.innerHTML =
            "<div><p class=\"activity-title\">" +
            payload.action +
            "</p><p class=\"activity-subtitle\">" +
            actor +
            " / " +
            payload.actorRole +
            " • " +
            payload.entityType +
            " " +
            payload.entityId +
            "</p></div>";

        adminFeed.prepend(item);
        while (adminFeed.children.length > 8) {
            adminFeed.removeChild(adminFeed.lastElementChild);
        }
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/banking")
        .withAutomaticReconnect([0, 1500, 5000, 10000])
        .build();

    connection.on("notification", (payload) => {
        showToast(payload.severity === "warning" ? "warn" : "info", payload.title || "Notification", payload.message || "");
    });

    connection.on("transfer.status", (payload) => {
        showToast(payload.isWarning ? "warn" : "info", "Transfer Update", payload.message || "Transfer updated.");
    });

    connection.on("fraud.alert", (payload) => {
        showToast("warn", "Fraud Alert", payload.message || "Transfer requires review.");
    });

    connection.on("account.balance", (payload) => {
        updateBalances(payload);
        if (payload.message) {
            showToast("info", "Balance Updated", payload.message);
        }
    });

    connection.on("audit.feed", (payload) => {
        appendAdminAudit(payload);
        if (body.dataset.admin === "true") {
            showToast("info", "Admin Feed", payload.action + " on " + payload.entityType);
        }
    });

    connection.onreconnecting(() => setStatus("Live reconnecting", "status-warn"));
    connection.onreconnected(() => setStatus("Live connected", "status-live"));
    connection.onclose(() => setStatus("Live offline", "status-offline"));

    setStatus("Live connecting");
    connection.start()
        .then(() => setStatus("Live connected", "status-live"))
        .catch(() => setStatus("Live unavailable", "status-offline"));
}());
