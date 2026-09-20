package com.voidstrap.android;

import android.content.Context;
import android.text.method.LinkMovementMethod;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.button.MaterialButtonToggleGroup;
import com.google.android.material.imageview.ShapeableImageView;
import com.google.android.material.textfield.TextInputEditText;

import java.io.IOException;
import java.text.DateFormat;
import java.text.NumberFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.Future;

final class ModsPacksTab {
    private static final int TYPE_CARD = 0;
    private static final int TYPE_MORE = 1;

    private final ModsFragment host;
    private final Context c;
    private final TextView status;
    private final LinearLayout filters;
    private final Adapter adapter = new Adapter();
    private final List<ModCatalog.Entry> entries = new ArrayList<>();
    private final Set<Long> seen = new HashSet<>();
    private boolean marketplace;
    private String sort = ModCatalog.GB_SORTS[0];
    private String query = "";
    private ModCatalog.Category category;
    private List<ModCatalog.Category> categories;
    private int page;
    private boolean hasMore;
    private boolean loading;
    private boolean loaded;
    private int generation;
    private Future<?> task;
    private final Runnable search;

    ModsPacksTab(ModsFragment host, View root) {
        this.host = host;
        this.c = root.getContext();
        RecyclerView list = (RecyclerView) root;
        list.setLayoutManager(new LinearLayoutManager(c));
        View v = LayoutInflater.from(c).inflate(R.layout.view_mods_packs_header, list, false);
        status = v.findViewById(R.id.packs_status);
        filters = v.findViewById(R.id.packs_filters);
        list.setAdapter(new androidx.recyclerview.widget.ConcatAdapter(new HeaderAdapter(v), adapter));
        MaterialButtonToggleGroup source = v.findViewById(R.id.packs_source);
        source.check(R.id.packs_gamebanana);
        source.addOnButtonCheckedListener((g, id, checked) -> {
            if (!checked) return;
            boolean m = id == R.id.packs_marketplace;
            if (m == marketplace) return;
            marketplace = m;
            sort = marketplace ? ModCatalog.MARKET_SORTS[0] : ModCatalog.GB_SORTS[0];
            category = null;
            buildFilters();
            load(true);
        });
        TextInputEditText box = v.findViewById(R.id.packs_search);
        search = () -> {
            String q = box.getText() == null ? "" : box.getText().toString().trim();
            if (q.equals(query)) return;
            query = q;
            if (!marketplace && !q.isEmpty() && q.length() < ModCatalog.MIN_SEARCH) return;
            load(true);
        };
        box.addTextChangedListener(new LibraryFragment.Watcher(() -> {
            box.removeCallbacks(search);
            box.postDelayed(search, 450);
        }));
        buildFilters();
    }

    void refresh() {
        if (!loaded && !loading) load(true);
    }

    void destroy() {
        if (task != null) task.cancel(true);
    }

    private void buildFilters() {
        filters.removeAllViews();
        String[] sorts = marketplace ? ModCatalog.MARKET_SORTS : ModCatalog.GB_SORTS;
        String[] sortLabels = c.getResources().getStringArray(marketplace ? R.array.mods_packs_market_sorts : R.array.mods_packs_sorts);
        int sel = 0;
        for (int i = 0; i < sorts.length; i++) if (sorts[i].equals(sort)) sel = i;
        SettingRows.choice(filters, c.getString(R.string.mods_packs_sort), null, sortLabels, sel, i -> {
            sort = sorts[i];
            load(true);
        });
        if (marketplace) return;
        List<String> labels = new ArrayList<>();
        labels.add(c.getString(R.string.mods_packs_all_categories));
        int csel = 0;
        if (categories != null) for (int i = 0; i < categories.size(); i++) {
            ModCatalog.Category cat = categories.get(i);
            labels.add(c.getString(R.string.mods_packs_category, cat.name, cat.count));
            if (category != null && category.id == cat.id) csel = i + 1;
        }
        SettingRows.choice(filters, c.getString(R.string.mods_packs_categories), categories == null ? c.getString(R.string.mods_packs_categories_loading) : null, labels.toArray(new String[0]), csel, i -> {
            category = i == 0 || categories == null ? null : categories.get(i - 1);
            load(true);
        });
        if (categories == null) loadCategories();
    }

    private void loadCategories() {
        host.store().work.execute(() -> {
            List<ModCatalog.Category> list;
            try {
                list = ModCatalog.categories();
            } catch (IOException | RuntimeException e) {
                return;
            }
            host.store().main.post(() -> {
                categories = list;
                if (host.isAdded() && !marketplace) buildFilters();
            });
        });
    }

    private void load(boolean reset) {
        if (task != null) task.cancel(true);
        int gen = ++generation;
        if (reset) {
            int old = adapter.getItemCount();
            entries.clear();
            seen.clear();
            page = 0;
            hasMore = false;
            adapter.notifyItemRangeRemoved(0, old);
        }
        loading = true;
        status.setText(c.getString(R.string.mods_packs_loading, marketplace ? ModCatalog.MARKETPLACE : ModCatalog.GAMEBANANA));
        int next = page + 1;
        boolean m = marketplace;
        String s = sort;
        String q = query;
        ModCatalog.Category cat = category;
        Context app = c.getApplicationContext();
        task = host.store().work.submit(() -> {
            ModCatalog.Page result = null;
            String error = null;
            try {
                result = m ? ModCatalog.marketplace(s, q) : ModCatalog.browse(app, next, s, cat, q);
            } catch (IOException | RuntimeException e) {
                error = app.getString(R.string.mods_packs_offline);
            }
            ModCatalog.Page r = result;
            String err = error;
            host.store().main.post(() -> {
                if (gen != generation || !host.isAdded()) return;
                loading = false;
                loaded = true;
                if (r == null) {
                    status.setText(err);
                    if (hasMore) adapter.notifyItemChanged(entries.size());
                    return;
                }
                page = next;
                int before = entries.size();
                boolean hadMore = hasMore;
                for (ModCatalog.Entry e : r.entries) if (seen.add(e.id)) entries.add(e);
                hasMore = !m && r.returned > 0 && (long) page * r.pageSize < r.total;
                if (hadMore) adapter.notifyItemRemoved(before);
                adapter.notifyItemRangeInserted(before, adapter.getItemCount() - before);
                status.setText(entries.isEmpty() ? c.getString(R.string.mods_packs_none)
                        : c.getResources().getQuantityString(R.plurals.mods_packs_shown, entries.size(), entries.size()));
                if (entries.size() == before && hasMore && r.returned > 0) load(false);
            });
        });
    }

    private String meta(ModCatalog.Entry e) {
        StringBuilder sb = new StringBuilder();
        if (!e.author.isEmpty()) sb.append(e.author);
        String cat = e.category.isEmpty() ? e.superCategory : e.category;
        if (!cat.isEmpty()) sb.append(sb.length() > 0 ? " · " : "").append(cat);
        NumberFormat nf = NumberFormat.getIntegerInstance();
        if (e.likes > 0) sb.append(" · ").append(c.getString(R.string.mods_packs_likes, nf.format(e.likes)));
        if (e.downloads > 0) sb.append(" · ").append(c.getString(R.string.mods_packs_downloads, nf.format(e.downloads)));
        if (!e.version.isEmpty()) sb.append(" · ").append(e.version);
        if (e.updated > 0) sb.append(" · ").append(Ui.ago(c, e.updated));
        return sb.toString();
    }

    private void open(ModCatalog.Entry e) {
        Context app = c.getApplicationContext();
        View content = LayoutInflater.from(c).inflate(R.layout.view_progress, null, false);
        ((TextView) content.findViewById(R.id.progress_text)).setText(R.string.mods_packs_opening);
        androidx.appcompat.app.AlertDialog progress = Ui.alert(c).setView(content).setCancelable(true).show();
        host.store().work.execute(() -> {
            ModCatalog.Entry d;
            String error = null;
            try {
                d = ModCatalog.detail(app, e);
                if (d == null) error = app.getString(R.string.mods_packs_hidden);
            } catch (IOException | RuntimeException ex) {
                d = null;
                error = app.getString(R.string.mods_packs_open_failed);
            }
            ModCatalog.Entry detail = d;
            String err = error;
            host.store().main.post(() -> {
                if (!progress.isShowing()) return;
                progress.dismiss();
                if (!host.isAdded()) return;
                if (detail == null) Ui.say(host.host(), err);
                else showDetail(detail);
            });
        });
    }

    private void showDetail(ModCatalog.Entry e) {
        View v = LayoutInflater.from(c).inflate(R.layout.view_mod_detail, new android.widget.FrameLayout(c), false);
        RecyclerView gallery = v.findViewById(R.id.detail_gallery);
        gallery.setLayoutManager(new LinearLayoutManager(c, LinearLayoutManager.HORIZONTAL, false));
        gallery.setAdapter(new Gallery(e.images));
        gallery.setVisibility(e.images.isEmpty() ? View.GONE : View.VISIBLE);
        String cat = e.category.isEmpty() ? e.superCategory : e.category;
        ((TextView) v.findViewById(R.id.detail_meta)).setText(c.getString(R.string.mods_packs_detail_meta, cat, e.author.isEmpty() ? e.source : e.author));
        NumberFormat nf = NumberFormat.getIntegerInstance();
        StringBuilder stats = new StringBuilder();
        if (e.downloads > 0) stats.append(c.getString(R.string.mods_packs_downloads, nf.format(e.downloads)));
        if (e.likes > 0) stats.append(stats.length() > 0 ? " · " : "").append(c.getString(R.string.mods_packs_likes, nf.format(e.likes)));
        if (e.views > 0) stats.append(stats.length() > 0 ? " · " : "").append(c.getString(R.string.mods_packs_views, nf.format(e.views)));
        if (e.updated > 0) stats.append(stats.length() > 0 ? " · " : "").append(c.getString(R.string.mods_packs_updated, DateFormat.getDateInstance(DateFormat.MEDIUM).format(new Date(e.updated))));
        TextView statsView = v.findViewById(R.id.detail_stats);
        statsView.setText(stats);
        statsView.setVisibility(stats.length() == 0 ? View.GONE : View.VISIBLE);
        View submitter = v.findViewById(R.id.detail_submitter);
        if (e.submitter != null && !e.submitter.name.isEmpty()) {
            ((TextView) v.findViewById(R.id.detail_author)).setText(e.submitter.name);
            String detail = e.submitter.title.isEmpty() ? "" : e.submitter.title;
            if (e.submitter.points > 0) detail += (detail.isEmpty() ? "" : " · ") + c.getString(R.string.mods_packs_points, nf.format(e.submitter.points));
            ((TextView) v.findViewById(R.id.detail_author_detail)).setText(detail);
            ShapeableImageView avatar = v.findViewById(R.id.detail_avatar);
            if (e.submitter.avatar.startsWith("https://")) Net.image(avatar, e.submitter.avatar, R.drawable.ic_people, Ui.dp(c, 40));
            else avatar.setImageResource(R.drawable.ic_people);
        } else {
            ((TextView) v.findViewById(R.id.detail_author)).setText(e.author.isEmpty() ? e.source : e.author);
            ((TextView) v.findViewById(R.id.detail_author_detail)).setText(e.source);
            ((ShapeableImageView) v.findViewById(R.id.detail_avatar)).setImageResource(R.drawable.ic_people);
        }
        v.findViewById(R.id.detail_page).setOnClickListener(x -> Ui.openWeb(c, e.profileUrl));
        submitter.setVisibility(View.VISIBLE);
        List<ModCatalog.ModFile> files = new ArrayList<>();
        for (ModCatalog.ModFile f : e.files) if (ModCatalog.inspectFile(f) == null) files.add(f);
        LinearLayout fileBox = v.findViewById(R.id.detail_files);
        TextView hint = v.findViewById(R.id.detail_hint);
        MaterialButton install = v.findViewById(R.id.detail_install);
        ModCatalog.ModFile[] chosen = {files.isEmpty() ? null : files.get(0)};
        Runnable updateHint = () -> {
            ModCatalog.ModFile f = chosen[0];
            if (f == null) {
                hint.setText(R.string.mods_packs_no_files);
                install.setEnabled(false);
                return;
            }
            String block = ModCatalog.androidBlock(e, f);
            StringBuilder h = new StringBuilder();
            if (f.size > 0) h.append(Ui.size(c, f.size));
            if (block != null) h.append(h.length() > 0 ? "\n" : "").append(block);
            else if (f.listingKnown && !f.listingRoblox && f.listingCache) h.append(h.length() > 0 ? "   " : "").append(c.getString(R.string.mods_packs_cache_hint));
            else h.append(h.length() > 0 ? "   " : "").append(c.getString(R.string.mods_packs_install_hint));
            if (!f.description.isEmpty()) h.append('\n').append(ModCatalog.plain(f.description));
            hint.setText(h);
            install.setEnabled(block == null);
        };
        if (files.size() > 1) {
            String[] names = new String[files.size()];
            for (int i = 0; i < names.length; i++) names[i] = files.get(i).name;
            SettingRows.choice(fileBox, c.getString(R.string.mods_packs_file), null, names, 0, i -> {
                chosen[0] = files.get(i);
                updateHint.run();
            });
        } else if (files.size() == 1) {
            TextView one = new TextView(c);
            one.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
            one.setText(files.get(0).name);
            one.setPadding(0, Ui.dp(c, 6), 0, 0);
            fileBox.addView(one);
        }
        updateHint.run();
        TextView desc = v.findViewById(R.id.detail_description);
        desc.setText(e.html.isEmpty() ? c.getString(R.string.mods_packs_no_description) : android.text.Html.fromHtml(e.html, android.text.Html.FROM_HTML_MODE_COMPACT));
        desc.setMovementMethod(LinkMovementMethod.getInstance());
        TextView license = v.findViewById(R.id.detail_license);
        StringBuilder lic = new StringBuilder(e.license);
        for (String[] rule : e.licenseRules) lic.append(lic.length() > 0 ? "\n" : "").append(rule[0]).append(": ").append(rule[1]);
        license.setText(lic);
        boolean hasLicense = lic.length() > 0;
        license.setVisibility(hasLicense ? View.VISIBLE : View.GONE);
        v.findViewById(R.id.detail_license_title).setVisibility(hasLicense ? View.VISIBLE : View.GONE);
        LinearLayout sections = v.findViewById(R.id.detail_sections);
        if (ModCatalog.GAMEBANANA.equals(e.source)) {
            section(sections, c.getString(R.string.mods_packs_comments, e.commentCount), e.comments, R.string.mods_packs_no_comments);
            section(sections, c.getString(R.string.mods_packs_updates, e.updateCount), e.updates, R.string.mods_packs_no_updates);
            section(sections, c.getString(R.string.mods_packs_issues, e.issueCount), e.issues, R.string.mods_packs_no_issues);
        }
        android.app.Dialog d = Ui.surface(host.host(), e.name, e.summary.length() > 160 ? e.summary.substring(0, 160) + "..." : e.summary, v, host::publishPresence);
        String byline = e.author.isEmpty() ? e.source : "by " + e.author;
        AppPresence.set(R.id.nav_mods, "Viewing " + e.name, e.summary.trim().isEmpty() ? byline : ModCatalog.plain(e.summary), e.iconUrl, e.name + (byline.isEmpty() ? "" : " " + byline), "View mod", e.profileUrl);
        install.setOnClickListener(x -> {
            ModCatalog.ModFile f = chosen[0];
            if (f == null) return;
            d.dismiss();
            host.runTask(R.string.mods_packs_installing, (app, cancel) -> {
                ManagedMods.Record r = ModCatalog.install(app, e, f, cancel, null);
                host.store().main.post(() -> {
                    if (host.isAdded()) host.selectTab(ModsFragment.TAB_LIBRARY);
                });
                return app.getString(R.string.mods_library_added, r.name);
            });
        });
        d.show();
    }

    private void section(LinearLayout parent, String title, List<ModCatalog.Post> posts, int empty) {
        Row header = Row.inflate(parent);
        header.set(R.drawable.ic_megaphone, title, null);
        header.chevron.setVisibility(View.VISIBLE);
        LinearLayout body = new LinearLayout(c);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(Ui.dp(c, 12), 0, 0, Ui.dp(c, 8));
        body.setVisibility(View.GONE);
        header.view.setOnClickListener(x -> {
            boolean open = body.getVisibility() != View.VISIBLE;
            body.setVisibility(open ? View.VISIBLE : View.GONE);
            header.chevron.setRotation(open ? 90 : 0);
        });
        parent.addView(header.view);
        parent.addView(body);
        if (posts.isEmpty()) {
            TextView none = new TextView(c);
            none.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
            none.setText(empty);
            body.addView(none);
            return;
        }
        DateFormat df = DateFormat.getDateInstance(DateFormat.MEDIUM);
        for (ModCatalog.Post p : posts) {
            TextView head = new TextView(c);
            head.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
            StringBuilder h = new StringBuilder();
            if (!p.title.isEmpty()) h.append(p.title);
            if (!p.author.isEmpty()) h.append(h.length() > 0 ? " · " : "").append(p.author);
            if (!p.status.isEmpty()) h.append(h.length() > 0 ? " · " : "").append(p.status);
            if (p.time > 0) h.append(h.length() > 0 ? " · " : "").append(df.format(new Date(p.time)));
            head.setText(h);
            head.setPadding(0, Ui.dp(c, 8), 0, Ui.dp(c, 2));
            body.addView(head);
            if (!p.body.isEmpty()) {
                TextView text = new TextView(c);
                text.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
                text.setText(p.body);
                text.setTextIsSelectable(true);
                body.addView(text);
            }
        }
    }

    private final class Gallery extends RecyclerView.Adapter<RecyclerView.ViewHolder> {
        private final List<ModCatalog.Image> images;

        Gallery(List<ModCatalog.Image> images) {
            this.images = images;
        }

        @NonNull
        @Override
        public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            ShapeableImageView iv = new ShapeableImageView(c);
            RecyclerView.LayoutParams lp = new RecyclerView.LayoutParams(Ui.dp(c, 220), ViewGroup.LayoutParams.MATCH_PARENT);
            lp.setMarginEnd(Ui.dp(c, 8));
            iv.setLayoutParams(lp);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            iv.setBackgroundResource(R.drawable.vs_icon_tile);
            iv.setShapeAppearanceModel(iv.getShapeAppearanceModel().withCornerSize(Ui.dp(c, 8)));
            return new RecyclerView.ViewHolder(iv) {
            };
        }

        @Override
        public void onBindViewHolder(@NonNull RecyclerView.ViewHolder h, int position) {
            ModCatalog.Image img = images.get(position);
            ShapeableImageView iv = (ShapeableImageView) h.itemView;
            iv.setContentDescription(img.caption == null || img.caption.isEmpty() ? c.getString(R.string.mods_packs_image) : img.caption);
            Net.image(iv, img.thumb, R.drawable.ic_image, Ui.dp(c, 220));
            iv.setOnClickListener(x -> Ui.openWeb(c, img.full));
        }

        @Override
        public int getItemCount() {
            return images.size();
        }
    }

    private final class Adapter extends RecyclerView.Adapter<RecyclerView.ViewHolder> {
        @Override
        public int getItemViewType(int position) {
            return position < entries.size() ? TYPE_CARD : TYPE_MORE;
        }

        @NonNull
        @Override
        public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            if (viewType == TYPE_MORE) {
                MaterialButton more = new MaterialButton(parent.getContext(), null, androidx.appcompat.R.attr.borderlessButtonStyle);
                more.setText(R.string.mods_packs_more);
                more.setIconResource(R.drawable.ic_chevron_down);
                RecyclerView.LayoutParams lp = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                more.setLayoutParams(lp);
                return new RecyclerView.ViewHolder(more) {
                };
            }
            return new RecyclerView.ViewHolder(LayoutInflater.from(parent.getContext()).inflate(R.layout.item_mod_card, parent, false)) {
            };
        }

        @Override
        public void onBindViewHolder(@NonNull RecyclerView.ViewHolder h, int position) {
            if (getItemViewType(position) == TYPE_MORE) {
                MaterialButton more = (MaterialButton) h.itemView;
                more.setEnabled(!loading);
                more.setOnClickListener(x -> load(false));
                return;
            }
            ModCatalog.Entry e = entries.get(position);
            View v = h.itemView;
            ((TextView) v.findViewById(R.id.card_title)).setText(e.name);
            TextView summary = v.findViewById(R.id.card_summary);
            summary.setText(e.summary);
            summary.setVisibility(e.summary.isEmpty() ? View.GONE : View.VISIBLE);
            ((TextView) v.findViewById(R.id.card_meta)).setText(meta(e));
            ShapeableImageView image = v.findViewById(R.id.card_image);
            if (e.iconUrl.startsWith("https://")) Net.image(image, e.iconUrl, R.drawable.ic_image, Ui.dp(c, 96));
            else image.setImageResource(R.drawable.ic_image);
            v.findViewById(R.id.card_page).setOnClickListener(x -> Ui.openWeb(c, e.profileUrl));
            v.findViewById(R.id.card_view).setOnClickListener(x -> open(e));
            v.setOnClickListener(x -> open(e));
        }

        @Override
        public int getItemCount() {
            return entries.size() + (hasMore ? 1 : 0);
        }
    }
}
