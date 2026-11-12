/*
 * The offline reader (spec 030 US3 scenario 2, FR-012). The offline page loads this plain script, which the service
 * worker keeps, so it runs with no connection and without the app's own code. It opens the encrypted cache that
 * lib/offline.ts fills (database "tb-offline", store "cache", record "current"), deletes it when it has expired by the
 * server time recorded at caching, and otherwise lists the kept items and shows one at a time under a banner with the
 * time of the last update. Text is written with textContent only, so nothing in an item can run.
 */
(function () {
  "use strict";
  var root = document.getElementById("offline-reader");
  if (!root || !("indexedDB" in window) || !window.crypto || !window.crypto.subtle) return;
  var labels = root.dataset;
  var locale = document.documentElement.lang || undefined;
  var day = 86400000;

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function wipe() {
    try {
      indexedDB.deleteDatabase("tb-offline");
    } catch (e) {
      /* nothing kept */
    }
  }

  function read() {
    return new Promise(function (resolve) {
      var request = indexedDB.open("tb-offline", 1);
      request.onupgradeneeded = function () {
        // No cache was ever made here: leave no empty database behind.
        request.transaction.abort();
      };
      request.onerror = function () {
        resolve(null);
      };
      request.onsuccess = function () {
        var db = request.result;
        if (!db.objectStoreNames.contains("cache")) {
          db.close();
          resolve(null);
          return;
        }
        var get = db.transaction("cache", "readonly").objectStore("cache").get("current");
        get.onsuccess = function () {
          db.close();
          resolve(get.result || null);
        };
        get.onerror = function () {
          db.close();
          resolve(null);
        };
      };
    });
  }

  function runs(parent, children) {
    (children || []).forEach(function (run) {
      var marks = run.marks || [];
      var node = el(marks.indexOf("bold") >= 0 ? "strong" : marks.indexOf("italic") >= 0 ? "em" : "span", null, run.text);
      parent.appendChild(node);
    });
  }

  function body(container, document) {
    ((document && document.blocks) || []).forEach(function (block) {
      var node;
      switch (block.type) {
        case "heading":
          node = el("h" + Math.min(Math.max(block.level || 2, 2), 4));
          runs(node, block.children);
          break;
        case "list":
          node = el(block.ordered ? "ol" : "ul");
          (block.items || []).forEach(function (item) {
            var li = el("li");
            runs(li, item);
            node.appendChild(li);
          });
          break;
        case "quote":
          node = el("blockquote");
          runs(node, block.children);
          break;
        case "table":
          node = el("table", "offline-table");
          (block.rows || []).forEach(function (row) {
            var tr = el("tr");
            row.forEach(function (cell) {
              var td = el("td");
              runs(td, cell);
              tr.appendChild(td);
            });
            node.appendChild(tr);
          });
          break;
        case "image":
          node = block.alt ? el("p", "field-hint", block.alt) : null;
          break;
        case "link":
          node = el("p", null, block.text);
          break;
        case "embed":
          node = null;
          break;
        default:
          node = el("p");
          runs(node, block.children);
      }
      if (node) container.appendChild(node);
    });
  }

  function banner(syncedAt) {
    var when = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(syncedAt));
    return el("p", "notice offline-banner", (labels.banner || "").replace("{time}", when));
  }

  function decrypt(cache, item) {
    return crypto.subtle.decrypt({ name: "AES-GCM", iv: item.iv }, cache.key, item.data).then(function (plain) {
      return JSON.parse(new TextDecoder().decode(plain));
    });
  }

  function show(cache, items) {
    root.replaceChildren();
    var section = el("section", "offline-library");
    section.setAttribute("aria-labelledby", "offline-library-title");
    var heading = el("h2", null, labels.title);
    heading.id = "offline-library-title";
    section.appendChild(heading);
    section.appendChild(banner(cache.syncedAt));
    var list = el("ul", "offline-list");
    items.forEach(function (item) {
      var li = el("li");
      var button = el("button", "offline-entry");
      button.type = "button";
      button.appendChild(el("span", "offline-kind", item.kind === "policy" ? labels.policy : labels.announcement));
      button.appendChild(el("span", "offline-entry-title", item.title));
      button.addEventListener("click", function () {
        open(cache, items, item);
      });
      li.appendChild(button);
      list.appendChild(li);
    });
    section.appendChild(list);
    root.appendChild(section);
  }

  function open(cache, items, item) {
    root.replaceChildren();
    var article = el("article", "offline-item");
    article.lang = item.locale;
    article.appendChild(banner(cache.syncedAt));
    var back = el("button", "button secondary", labels.back);
    back.type = "button";
    back.addEventListener("click", function () {
      show(cache, items);
    });
    article.appendChild(back);
    article.appendChild(el("p", "offline-kind", item.kind === "policy" ? labels.policy : labels.announcement));
    var title = el("h2", null, item.title);
    title.tabIndex = -1;
    article.appendChild(title);
    if (item.summary) article.appendChild(el("p", "field-hint", item.summary));
    var text = el("div", "offline-body");
    body(text, item.body);
    article.appendChild(text);
    root.appendChild(article);
    title.focus();
  }

  read().then(function (cache) {
    if (!cache || !cache.items || cache.items.length === 0) return;
    var now = Date.now();
    var synced = Date.parse(cache.syncedAt);
    // Expiry follows the server time recorded at caching; a clock set back before that time does not extend it.
    if (!(now <= Date.parse(cache.expiresAt)) || now < synced - day) {
      wipe();
      return;
    }
    Promise.all(
      cache.items.map(function (item) {
        return decrypt(cache, item).catch(function () {
          return null;
        });
      }),
    ).then(function (items) {
      var readable = items.filter(Boolean);
      if (readable.length > 0) show(cache, readable);
    });
  });
})();
