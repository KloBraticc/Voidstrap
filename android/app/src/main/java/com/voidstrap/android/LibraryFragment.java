package com.voidstrap.android;

import android.content.Context;
import android.os.Bundle;
import android.text.format.DateUtils;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TableLayout;
import android.widget.TableRow;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.appcompat.app.AlertDialog;
import androidx.constraintlayout.helper.widget.Flow;
import androidx.constraintlayout.widget.ConstraintLayout;
import androidx.core.widget.NestedScrollView;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.progressindicator.LinearProgressIndicator;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;

import java.text.DateFormat;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

public final class LibraryFragment extends Page {
    static String incoming;

    private static final long DAY = 24 * 60 * 60 * 1000L;

    private LinearLayout side;
    private TextInputEditText search;
    private TextInputEditText add;
    private TextView status;
    private LinearLayout dash;
    private LinearLayout dashHeader;
    private NestedScrollView dashboard;
    private NestedScrollView detailScroll;
    private View detail;
    private LinearProgressIndicator loading;
    private final SideAdapter sideAdapter = new SideAdapter();

    private List<LibraryData.Game> games = new ArrayList<>();
    private List<LibraryData.Event> events = new ArrayList<>();
    private final Map<Long, LibraryData.Game> known = new HashMap<>();
    private final Map<Long, List<LibraryData.Pass>> passCache = new HashMap<>();
    private LibraryData.Game selected;
    private boolean storeTab;
    private boolean pinnedOpen = true;
    private boolean allOpen = true;
    private String query = "";
    private String loadedSignature = "";
    private int loadSeq;
    private int retries;
    private final Runnable retry = () -> {
        if (getView() != null) load(true);
    };
    private final Runnable applySearch = this::render;

    private final androidx.activity.OnBackPressedCallback closeDetail = new androidx.activity.OnBackPressedCallback(false) {
        @Override
        public void handleOnBackPressed() {
            select(null);
        }
    };

    public LibraryFragment() {
        super(R.layout.fragment_library);
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        side = v.findViewById(R.id.lib_side);
        search = v.findViewById(R.id.lib_search);
        add = v.findViewById(R.id.lib_add);
        status = v.findViewById(R.id.lib_status);
        dash = v.findViewById(R.id.lib_dash);
        dashboard = v.findViewById(R.id.lib_dashboard);
        detailScroll = v.findViewById(R.id.lib_detail_scroll);
        detail = v.findViewById(R.id.lib_detail);
        loading = v.findViewById(R.id.lib_loading);
        RecyclerView list = v.findViewById(R.id.lib_list);
        list.setLayoutManager(new LinearLayoutManager(requireContext()));
        list.setAdapter(sideAdapter);
        list.setItemAnimator(null);
        v.findViewById(R.id.lib_home).setOnClickListener(x -> select(null));
        v.findViewById(R.id.lib_refresh).setOnClickListener(x -> load(true));
        v.findViewById(R.id.lib_add_button).setOnClickListener(x -> addFromText());
        add.setOnEditorActionListener((tv, action, ev) -> {
            if (action != EditorInfo.IME_ACTION_DONE) return false;
            addFromText();
            return true;
        });
        search.addTextChangedListener(new Watcher(() -> {
            query = search.getText() == null ? "" : search.getText().toString().trim().toLowerCase(Locale.ROOT);
            store.main.removeCallbacks(applySearch);
            store.main.postDelayed(applySearch, 150);
        }));
        android.content.res.Configuration conf = getResources().getConfiguration();
        if (conf.screenWidthDp < 840 || conf.screenHeightDp < 480) {
            dashHeader = new LinearLayout(requireContext());
            dashHeader.setOrientation(LinearLayout.VERTICAL);
            View searchRow = v.findViewById(R.id.lib_search_row);
            View addRow = v.findViewById(R.id.lib_add_row);
            ((ViewGroup) searchRow.getParent()).removeView(searchRow);
            ((ViewGroup) addRow.getParent()).removeView(addRow);
            ((ViewGroup) addRow).getChildAt(0).setVisibility(View.GONE);
            int inset = getResources().getDimensionPixelSize(R.dimen.vs_page_inset);
            v.setPaddingRelative(0, v.getPaddingTop(), 0, v.getPaddingBottom());
            dash.setPaddingRelative(inset, 0, inset, 0);
            dash.setClipToPadding(false);
            detailScroll.setPaddingRelative(inset, detailScroll.getPaddingTop(), inset, detailScroll.getPaddingBottom());
            dashHeader.addView(searchRow);
            dashHeader.addView(addRow);
            side.setVisibility(View.GONE);
        }
        requireActivity().getOnBackPressedDispatcher().addCallback(getViewLifecycleOwner(), closeDetail);
    }

    @Override
    public void onDestroyView() {
        store.main.removeCallbacks(applySearch);
        store.main.removeCallbacks(retry);
        super.onDestroyView();
    }

    @Override
    public void onHiddenChanged(boolean hidden) {
        closeDetail.setEnabled(!hidden && selected != null);
        super.onHiddenChanged(hidden);
    }

    @Override
    protected void refresh() {
        if (incoming != null) {
            add.setText(incoming);
            incoming = null;
            addFromText();
        }
        load(false);
    }

    private void load(boolean force) {
        List<LibraryData.Game> base = LibraryData.base(store);
        for (LibraryData.Game g : base) carry(g, known.get(g.placeId));
        games = base;
        if (selected != null) selected = find(selected.placeId);
        render();
        StringBuilder sig = new StringBuilder();
        for (LibraryData.Game g : base) sig.append(g.placeId).append(g.pinned ? 'p' : 'h').append(',');
        if (!force && sig.toString().equals(loadedSignature)) return;
        if (!sig.toString().equals(loadedSignature)) retries = 0;
        loadedSignature = sig.toString();
        store.main.removeCallbacks(retry);
        int seq = ++loadSeq;
        loading.setVisibility(View.VISIBLE);
        Context app = requireContext().getApplicationContext();
        List<LibraryData.Game> copy = new ArrayList<>();
        for (LibraryData.Game g : base) {
            LibraryData.Game c = new LibraryData.Game();
            carry(c, g);
            c.placeId = g.placeId;
            c.pinned = g.pinned;
            c.lastPlayed = g.lastPlayed;
            c.plays = g.plays;
            copy.add(c);
        }
        store.work.execute(() -> {
            LibraryData.enrich(app, copy);
            List<LibraryData.Game> order = new ArrayList<>(copy);
            order.sort((x, y) -> x.pinned != y.pinned ? (x.pinned ? -1 : 1) : Long.compare(y.lastPlayed, x.lastPlayed));
            List<LibraryData.Event> found = LibraryData.events(app, order);
            store.main.post(() -> {
                if (seq != loadSeq || getView() == null) return;
                for (LibraryData.Game g : copy) known.put(g.placeId, g);
                for (LibraryData.Game g : games) carry(g, known.get(g.placeId));
                events = found;
                loading.setVisibility(View.GONE);
                savePinMeta();
                render();
                boolean missing = false;
                for (LibraryData.Game g : copy) missing |= g.name.isEmpty() || g.iconUrl == null;
                if (missing && retries < 3) store.main.postDelayed(retry, 5000L << (2 * retries++));
            });
        });
    }

    private static void carry(LibraryData.Game to, LibraryData.Game from) {
        if (from == null) return;
        if (to.universeId <= 0) to.universeId = from.universeId;
        if (from.name != null && !from.name.isEmpty()) to.name = from.name;
        if (from.iconUrl != null) to.iconUrl = from.iconUrl;
        to.thumbUrl = from.thumbUrl;
        to.creator = from.creator;
        to.description = from.description;
        to.genre = from.genre;
        to.playing = from.playing;
        to.visits = from.visits;
        to.maxPlayers = from.maxPlayers;
        to.created = from.created;
        to.updated = from.updated;
        to.likePercent = from.likePercent;
    }

    private void savePinMeta() {
        boolean changed = false;
        for (Store.Game p : store.library) {
            LibraryData.Game g = known.get(p.placeId);
            if (g == null) continue;
            if (!g.name.isEmpty() && !g.name.equals(p.name)) {
                p.name = Store.clip(g.name, 200);
                changed = true;
            }
            if (g.iconUrl != null && !g.iconUrl.equals(p.iconUrl)) {
                p.iconUrl = g.iconUrl;
                changed = true;
            }
            if (g.universeId > 0 && p.universeId != g.universeId) {
                p.universeId = g.universeId;
                changed = true;
            }
        }
        if (changed) store.saveLibrary();
    }

    private LibraryData.Game find(long placeId) {
        for (LibraryData.Game g : games) if (g.placeId == placeId) return g;
        return null;
    }

    private boolean matches(LibraryData.Game g) {
        return query.isEmpty() || g.title().toLowerCase(Locale.ROOT).contains(query) || String.valueOf(g.placeId).contains(query);
    }

    private List<LibraryData.Game> sortedByName(boolean pinned) {
        List<LibraryData.Game> out = new ArrayList<>();
        for (LibraryData.Game g : games) if (g.pinned == pinned && matches(g)) out.add(g);
        out.sort((x, y) -> x.title().compareToIgnoreCase(y.title()));
        return out;
    }

    private void select(LibraryData.Game g) {
        selected = g;
        storeTab = false;
        render();
        if (g != null) detailScroll.scrollTo(0, 0);
    }

    private void render() {
        if (getView() == null) return;
        List<Object> rows = new ArrayList<>();
        List<LibraryData.Game> pinned = sortedByName(true);
        List<LibraryData.Game> rest = sortedByName(false);
        if (!pinned.isEmpty()) {
            rows.add(Boolean.TRUE);
            if (pinnedOpen) rows.addAll(pinned);
        }
        if (!rest.isEmpty()) {
            rows.add(Boolean.FALSE);
            if (allOpen) rows.addAll(rest);
        }
        sideAdapter.set(rows, pinned.size(), rest.size());
        boolean showDetail = selected != null;
        closeDetail.setEnabled(showDetail);
        dashboard.setVisibility(showDetail ? View.GONE : View.VISIBLE);
        detailScroll.setVisibility(showDetail ? View.VISIBLE : View.GONE);
        if (showDetail) bindDetail(selected);
        else renderDashboard();
    }

    private String dashKey;

    private String dashKey() {
        StringBuilder k = new StringBuilder(query).append('|').append(dash.getWidth()).append('|');
        for (LibraryData.Event e : events) k.append(e.id).append(e.start).append(e.title).append(',');
        k.append('|');
        for (LibraryData.Game g : games) {
            k.append(g.placeId).append(g.pinned).append(g.lastPlayed).append(g.updated).append(g.title())
                    .append(g.iconUrl).append(g.thumbUrl).append(',');
        }
        return k.toString();
    }

    private void renderDashboard() {
        String key = dashKey();
        if (key.equals(dashKey) && dash.getChildCount() > 0) return;
        dashKey = key;
        if (dashHeader != null && dashHeader.getParent() == dash && dash.indexOfChild(dashHeader) == 0) {
            if (dash.getChildCount() > 1) dash.removeViews(1, dash.getChildCount() - 1);
        } else {
            dash.removeAllViews();
            if (dashHeader != null) {
                if (dashHeader.getParent() != null) ((ViewGroup) dashHeader.getParent()).removeView(dashHeader);
                dash.addView(dashHeader);
            }
        }
        if (games.isEmpty()) {
            TextView t = new TextView(requireContext());
            t.setText(R.string.library_empty_all);
            t.setTextColor(Ui.attr(requireContext(), R.attr.vsTextSecondary));
            t.setTextSize(14);
            t.setGravity(android.view.Gravity.CENTER);
            t.setMaxWidth(Ui.dp(requireContext(), 380));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            lp.gravity = android.view.Gravity.CENTER_HORIZONTAL;
            lp.topMargin = Ui.dp(requireContext(), 72);
            dash.addView(t, lp);
            return;
        }
        boolean searching = !query.isEmpty();
        if (!searching && !events.isEmpty()) {
            LinearLayout row = shelf(getString(R.string.library_events), nextTop());
            for (LibraryData.Event e : events) row.addView(eventCard(row, e));
        }
        List<LibraryData.Game> fresh = new ArrayList<>();
        long cutoff = System.currentTimeMillis() - 30 * DAY;
        for (LibraryData.Game g : games) if (g.updated >= cutoff) fresh.add(g);
        fresh.sort((x, y) -> Long.compare(y.updated, x.updated));
        if (!searching && !fresh.isEmpty()) {
            LinearLayout row = shelf(getString(R.string.library_whats_new), nextTop());
            for (int i = 0; i < fresh.size() && i < 12; i++) row.addView(shelfCard(row, fresh.get(i), updatedAgo(fresh.get(i).updated)));
        }
        List<LibraryData.Game> recent = new ArrayList<>();
        for (LibraryData.Game g : games) if (g.lastPlayed > 0) recent.add(g);
        recent.sort((x, y) -> Long.compare(y.lastPlayed, x.lastPlayed));
        if (!searching && !recent.isEmpty()) {
            LinearLayout row = shelf(getString(R.string.library_recent), nextTop());
            for (int i = 0; i < recent.size() && i < 12; i++) row.addView(shelfCard(row, recent.get(i), lastPlayed(recent.get(i).lastPlayed)));
        }
        List<View> tiles = new ArrayList<>();
        List<LibraryData.Game> all = new ArrayList<>(games);
        all.sort((x, y) -> x.title().compareToIgnoreCase(y.title()));
        for (LibraryData.Game g : all) if (matches(g)) tiles.add(tile(g));
        TextView header = sectionTitle(getString(R.string.library_all_games, searching ? tiles.size() : games.size()), nextTop());
        dash.addView(header);
        if (searching && tiles.isEmpty()) {
            TextView none = new TextView(requireContext());
            none.setText(R.string.library_no_match);
            none.setTextColor(Ui.attr(requireContext(), R.attr.vsTextSecondary));
            none.setTextSize(14);
            dash.addView(none);
            return;
        }
        ConstraintLayout grid = new ConstraintLayout(requireContext());
        dash.addView(grid, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        flow(grid, tiles);
    }

    private int nextTop() {
        return dash.getChildCount() > 0 ? 18 : 0;
    }

    private TextView sectionTitle(String text, int topMargin) {
        TextView t = new TextView(requireContext());
        t.setText(text);
        t.setTextSize(18);
        t.setTypeface(android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL));
        t.setTextColor(Ui.attr(requireContext(), R.attr.vsTextPrimary));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Ui.dp(requireContext(), topMargin);
        lp.bottomMargin = Ui.dp(requireContext(), 8);
        lp.setMarginStart(Ui.dp(requireContext(), 2));
        t.setLayoutParams(lp);
        return t;
    }

    private LinearLayout shelf(String title, int topMargin) {
        dash.addView(sectionTitle(title, topMargin));
        HorizontalScrollView scroll = new HorizontalScrollView(requireContext());
        scroll.setHorizontalScrollBarEnabled(false);
        LinearLayout row = new LinearLayout(requireContext());
        row.setOrientation(LinearLayout.HORIZONTAL);
        scroll.addView(row);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        int inset = getResources().getDimensionPixelSize(R.dimen.vs_page_inset);
        lp.setMarginEnd(-inset);
        scroll.setPaddingRelative(0, 0, inset, 0);
        if (dashHeader != null) {
            lp.setMarginStart(-inset);
            scroll.setPaddingRelative(inset, 0, inset, 0);
        }
        scroll.setClipToPadding(false);
        dash.addView(scroll, lp);
        return row;
    }

    private View shelfCard(ViewGroup parent, LibraryData.Game g, String subtitle) {
        View v = LayoutInflater.from(requireContext()).inflate(R.layout.item_library_shelf, parent, false);
        Net.image(v.findViewById(R.id.shelf_image), g.thumbUrl != null ? g.thumbUrl : g.iconUrl, 0, Ui.dp(requireContext(), 246));
        ((TextView) v.findViewById(R.id.shelf_title)).setText(g.title());
        ((TextView) v.findViewById(R.id.shelf_subtitle)).setText(subtitle);
        v.setOnClickListener(x -> select(g));
        Flyout.bind(v, () -> gameMenu(g));
        v.setContentDescription(g.title());
        return v;
    }

    private View eventCard(ViewGroup parent, LibraryData.Event e) {
        View v = LayoutInflater.from(requireContext()).inflate(R.layout.item_library_shelf, parent, false);
        Net.image(v.findViewById(R.id.shelf_image), e.thumbUrl, 0, Ui.dp(requireContext(), 246));
        TextView badge = v.findViewById(R.id.shelf_badge);
        badge.setVisibility(View.VISIBLE);
        badge.setText(e.start <= System.currentTimeMillis() ? getString(R.string.library_live)
                : DateUtils.formatDateTime(requireContext(), e.start, DateUtils.FORMAT_SHOW_DATE | DateUtils.FORMAT_SHOW_TIME | DateUtils.FORMAT_ABBREV_MONTH));
        ((TextView) v.findViewById(R.id.shelf_title)).setText(e.title);
        ((TextView) v.findViewById(R.id.shelf_subtitle)).setText(e.subtitle);
        MaterialButton action = v.findViewById(R.id.shelf_action);
        action.setVisibility(View.VISIBLE);
        action.setText(R.string.library_view_event);
        View.OnClickListener open = x -> Actions.openInRoblox(host(), "https://www.roblox.com/events/" + e.id);
        action.setOnClickListener(open);
        v.setOnClickListener(open);
        return v;
    }

    private View tile(LibraryData.Game g) {
        View v = LayoutInflater.from(requireContext()).inflate(R.layout.item_library_tile, dash, false);
        Net.image(v.findViewById(R.id.tile_image), g.iconUrl, 0, Ui.dp(requireContext(), 150));
        TextView tileTitle = v.findViewById(R.id.tile_title);
        tileTitle.setText(g.title());
        showPin(tileTitle, g.pinned);
        v.setOnClickListener(x -> select(g));
        Flyout.bind(v, () -> gameMenu(g));
        v.setContentDescription(g.title());
        return v;
    }

    private void flow(ConstraintLayout grid, List<View> views) {
        grid.removeAllViews();
        int[] ids = new int[views.size()];
        for (int i = 0; i < views.size(); i++) {
            View v = views.get(i);
            v.setId(View.generateViewId());
            ids[i] = v.getId();
            grid.addView(v, new ConstraintLayout.LayoutParams(v.getLayoutParams().width, ViewGroup.LayoutParams.WRAP_CONTENT));
        }
        Flow f = new Flow(requireContext());
        f.setId(View.generateViewId());
        f.setOrientation(Flow.HORIZONTAL);
        f.setWrapMode(Flow.WRAP_ALIGNED);
        f.setHorizontalStyle(Flow.CHAIN_PACKED);
        f.setHorizontalBias(0f);
        f.setFirstHorizontalBias(0f);
        f.setLastHorizontalBias(0f);
        f.setHorizontalAlign(Flow.HORIZONTAL_ALIGN_START);
        f.setVerticalAlign(Flow.VERTICAL_ALIGN_TOP);
        f.setHorizontalGap(Ui.dp(requireContext(), 10));
        f.setVerticalGap(Ui.dp(requireContext(), 10));
        ConstraintLayout.LayoutParams lp = new ConstraintLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.startToStart = ConstraintLayout.LayoutParams.PARENT_ID;
        lp.endToEnd = ConstraintLayout.LayoutParams.PARENT_ID;
        lp.topToTop = ConstraintLayout.LayoutParams.PARENT_ID;
        grid.addView(f, lp);
        f.setReferencedIds(ids);
        if (grid.getWidth() > 0) {
            fitTiles(f, views, grid.getWidth());
            return;
        }
        grid.addOnLayoutChangeListener(new View.OnLayoutChangeListener() {
            @Override
            public void onLayoutChange(View v, int l, int t, int r, int b, int ol, int ot, int or, int ob) {
                if (r - l <= 0) return;
                v.removeOnLayoutChangeListener(this);
                if (isAdded()) fitTiles(f, views, r - l);
            }
        });
    }

    private void fitTiles(Flow f, List<View> views, int width) {
        if (width <= 0 || views.isEmpty()) return;
        int gap = Ui.dp(requireContext(), 10);
        int cols = Math.max(2, (width + gap) / (Ui.dp(requireContext(), 150) + gap));
        int w = (width - gap * (cols - 1)) / cols;
        f.setMaxElementsWrap(cols);
        for (View v : views) {
            v.getLayoutParams().width = w;
            View image = v.findViewById(R.id.tile_image);
            image.getLayoutParams().height = w;
            image.requestLayout();
            v.requestLayout();
        }
    }

    private void bindDetail(LibraryData.Game g) {
        Context c = requireContext();
        Net.image(detail.findViewById(R.id.det_thumb), g.thumbUrl != null ? g.thumbUrl : g.iconUrl, 0, Ui.dp(c, 640));
        ((TextView) detail.findViewById(R.id.det_name)).setText(g.title());
        ((TextView) detail.findViewById(R.id.det_creator)).setText(g.creator.isEmpty() ? "" : getString(R.string.library_by, g.creator));
        detail.findViewById(R.id.det_play).setOnClickListener(x -> Actions.launch(host(), Deeplink.place(g.placeId, null, null), g.title()));
        MaterialButton pin = detail.findViewById(R.id.det_pin);
        MaterialButton removeButton = detail.findViewById(R.id.det_remove);
        boolean compact = !Ui.wide(c);
        String pinLabel = getString(g.pinned ? R.string.library_unpin : R.string.library_pin);
        pin.setText(compact ? null : pinLabel);
        pin.setContentDescription(pinLabel);
        removeButton.setText(compact ? null : getString(R.string.library_remove));
        removeButton.setContentDescription(getString(R.string.library_remove));
        for (MaterialButton b : new MaterialButton[]{pin, removeButton}) {
            b.setIconPadding(compact ? 0 : Ui.dp(c, 8));
            b.setMinimumWidth(compact ? Ui.dp(c, 48) : 0);
            b.setMinWidth(compact ? Ui.dp(c, 48) : 0);
        }
        pin.setIconTint(android.content.res.ColorStateList.valueOf(g.pinned ? Ui.attr(c, androidx.appcompat.R.attr.colorPrimary) : Ui.attr(c, R.attr.vsTextPrimary)));
        pin.setOnClickListener(x -> togglePin(g));
        removeButton.setOnClickListener(x -> remove(g));
        detail.findViewById(R.id.det_more).setOnClickListener(this::detailMenu);
        boolean below = dashboardWidthDp() < 800;
        LinearLayout inline = detail.findViewById(R.id.det_stats);
        LinearLayout under = detail.findViewById(R.id.det_stats_below);
        inline.removeAllViews();
        under.removeAllViews();
        LinearLayout stats = below ? under : inline;
        under.setVisibility(below ? View.VISIBLE : View.GONE);
        inline.setVisibility(below ? View.GONE : View.VISIBLE);
        detail.findViewById(R.id.det_space).setVisibility(below ? View.GONE : View.VISIBLE);
        stat(stats, R.string.library_last_played, lastPlayed(g.lastPlayed));
        stat(stats, R.string.library_times_played, String.valueOf(g.plays));
        stat(stats, R.string.library_playing_now, HomeFeed.count(g.playing));
        stat(stats, R.string.library_rating, g.likePercent < 0 ? "?" : g.likePercent + "%");
        bindTabs();
        ((TextView) detail.findViewById(R.id.det_description)).setText(g.description);
        TableLayout facts = detail.findViewById(R.id.det_facts);
        facts.removeAllViews();
        DateFormat date = DateFormat.getDateInstance(DateFormat.MEDIUM);
        fact(facts, R.string.library_creator, g.creator.isEmpty() ? getString(R.string.library_unknown) : g.creator);
        fact(facts, R.string.library_genre, g.genre.isEmpty() ? getString(R.string.library_unknown) : g.genre);
        fact(facts, R.string.library_max_players, g.maxPlayers > 0 ? String.valueOf(g.maxPlayers) : getString(R.string.library_unknown));
        fact(facts, R.string.library_visits, HomeFeed.count(g.visits));
        fact(facts, R.string.library_created, g.created > 0 ? date.format(g.created) : getString(R.string.library_unknown));
        fact(facts, R.string.library_updated, g.updated > 0 ? date.format(g.updated) : getString(R.string.library_unknown));
    }

    private int dashboardWidthDp() {
        View parent = (View) detailScroll.getParent();
        int px = parent.getWidth();
        if (px == 0) return getResources().getConfiguration().screenWidthDp;
        return Math.round(px / getResources().getDisplayMetrics().density);
    }

    private void stat(LinearLayout parent, int label, String value) {
        LinearLayout box = new LinearLayout(requireContext());
        box.setOrientation(LinearLayout.VERTICAL);
        TextView l = new TextView(requireContext());
        l.setText(label);
        l.setTextSize(10);
        l.setTypeface(android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL));
        l.setTextColor(Ui.attr(requireContext(), R.attr.vsTextTertiary));
        TextView v = new TextView(requireContext());
        v.setText(value);
        v.setTextSize(13);
        v.setTextColor(Ui.attr(requireContext(), R.attr.vsTextPrimary));
        box.addView(l);
        box.addView(v);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        if (parent.getChildCount() > 0) lp.setMarginStart(Ui.dp(requireContext(), 22));
        parent.addView(box, lp);
    }

    private void fact(TableLayout table, int label, String value) {
        TableRow row = new TableRow(requireContext());
        TextView l = new TextView(requireContext());
        l.setText(label);
        l.setTextSize(12);
        l.setTextColor(Ui.attr(requireContext(), R.attr.vsTextTertiary));
        l.setPadding(0, 0, Ui.dp(requireContext(), 18), Ui.dp(requireContext(), 6));
        TextView v = new TextView(requireContext());
        v.setText(value);
        v.setTextSize(12);
        v.setTextColor(Ui.attr(requireContext(), R.attr.vsTextPrimary));
        v.setPadding(0, 0, 0, Ui.dp(requireContext(), 6));
        row.addView(l);
        row.addView(v);
        table.addView(row);
    }

    private void bindTabs() {
        int on = Ui.attr(requireContext(), R.attr.vsTextPrimary);
        int off = Ui.attr(requireContext(), R.attr.vsTextSecondary);
        tab(R.id.det_tab_about_text, R.id.det_tab_about_line, R.drawable.ic_info, !storeTab, on, off);
        tab(R.id.det_tab_store_text, R.id.det_tab_store_line, R.drawable.ic_apps, storeTab, on, off);
        detail.findViewById(R.id.det_tab_about).setOnClickListener(x -> {
            storeTab = false;
            bindTabs();
        });
        detail.findViewById(R.id.det_tab_store).setOnClickListener(x -> {
            storeTab = true;
            bindTabs();
        });
        detail.findViewById(R.id.det_about).setVisibility(storeTab ? View.GONE : View.VISIBLE);
        detail.findViewById(R.id.det_store).setVisibility(storeTab ? View.VISIBLE : View.GONE);
        if (storeTab && selected != null) loadPasses(selected);
    }

    private void tab(int text, int line, int icon, boolean active, int on, int off) {
        TextView t = detail.findViewById(text);
        t.setTextColor(active ? on : off);
        android.graphics.drawable.Drawable d = androidx.core.content.ContextCompat.getDrawable(requireContext(), icon);
        if (d != null) {
            d = d.mutate();
            int size = Ui.dp(requireContext(), 16);
            d.setBounds(0, 0, size, size);
            d.setTint(active ? on : off);
        }
        t.setCompoundDrawablesRelative(d, null, null, null);
        detail.findViewById(line).setVisibility(active ? View.VISIBLE : View.INVISIBLE);
    }

    private void loadPasses(LibraryData.Game g) {
        View progress = detail.findViewById(R.id.det_store_loading);
        TextView note = detail.findViewById(R.id.det_store_status);
        ConstraintLayout grid = detail.findViewById(R.id.det_passes);
        List<LibraryData.Pass> cached = passCache.get(g.universeId);
        if (cached != null) {
            progress.setVisibility(View.GONE);
            showPasses(grid, note, cached);
            return;
        }
        grid.removeAllViews();
        note.setVisibility(View.GONE);
        progress.setVisibility(View.VISIBLE);
        if (g.universeId <= 0) {
            progress.setVisibility(View.GONE);
            showPasses(grid, note, new ArrayList<>());
            return;
        }
        Context app = requireContext().getApplicationContext();
        long universe = g.universeId;
        store.work.execute(() -> {
            List<LibraryData.Pass> found = LibraryData.passes(app, universe);
            store.main.post(() -> {
                passCache.put(universe, found);
                if (getView() == null || selected == null || selected.universeId != universe || !storeTab) return;
                progress.setVisibility(View.GONE);
                showPasses(grid, note, found);
            });
        });
    }

    private void showPasses(ConstraintLayout grid, TextView note, List<LibraryData.Pass> passes) {
        note.setVisibility(passes.isEmpty() ? View.VISIBLE : View.GONE);
        note.setText(R.string.library_no_passes);
        List<View> cards = new ArrayList<>();
        for (LibraryData.Pass p : passes) {
            View v = LayoutInflater.from(requireContext()).inflate(R.layout.item_library_tile, grid, false);
            Net.image(v.findViewById(R.id.tile_image), p.iconUrl, 0, Ui.dp(requireContext(), 150));
            TextView passTitle = v.findViewById(R.id.tile_title);
            passTitle.setText(p.name);
            passTitle.setGravity(android.view.Gravity.CENTER_HORIZONTAL);
            showPin(passTitle, false);
            TextView price = v.findViewById(R.id.tile_subtitle);
            price.setVisibility(View.VISIBLE);
            price.setText(p.price);
            v.setOnClickListener(x -> Actions.openInRoblox(host(), "https://www.roblox.com/game-pass/" + p.id));
            v.setContentDescription(p.name + ", " + p.price);
            cards.add(v);
        }
        flow(grid, cards);
    }

    private void togglePin(LibraryData.Game g) {
        if (g.pinned) {
            store.unpin(g.placeId);
            Notify.say(host(), Notify.LIBRARY, getString(R.string.game_unpinned, g.title()));
        } else {
            store.pin(new Store.Game(g.placeId, g.universeId, Store.clip(g.name, 200), g.iconUrl == null ? "" : g.iconUrl, System.currentTimeMillis()));
            Notify.say(host(), Notify.LIBRARY, getString(R.string.library_pinned_toast, g.title()));
        }
    }

    private void remove(LibraryData.Game g) {
        store.forget(g.placeId);
        known.remove(g.placeId);
        if (selected != null && selected.placeId == g.placeId) selected = null;
        Notify.say(host(), Notify.LIBRARY, getString(R.string.library_removed, g.title()));
        load(false);
    }

    private Flyout gameMenu(LibraryData.Game g) {
        return new Flyout(requireContext())
                .add(R.drawable.ic_play, R.string.library_play, () -> Actions.launch(host(), Deeplink.place(g.placeId, null, null), g.title()))
                .add(R.drawable.ic_pin, g.pinned ? R.string.library_unpin : R.string.library_pin, () -> togglePin(g))
                .separator()
                .add(R.drawable.ic_delete, R.string.library_remove_menu, () -> remove(g));
    }

    private void detailMenu(View anchor) {
        LibraryData.Game g = selected;
        if (g == null) return;
        String link = "https://www.roblox.com/games/" + g.placeId;
        new Flyout(requireContext())
                .add(R.drawable.ic_server, R.string.library_join_server, () -> joinServer(g))
                .add(R.drawable.ic_phone_add, R.string.library_add_shortcut, () -> Shortcuts.request(host(), Deeplink.place(g.placeId, null, null), g.title(), g.iconUrl))
                .add(R.drawable.ic_copy, R.string.game_copy_link, () -> {
                    Ui.copy(requireContext(), g.title(), link);
                    Notify.say(host(), Notify.COPY, R.string.game_link_copied);
                })
                .add(R.drawable.ic_globe, R.string.library_open_page, () -> Actions.openInRoblox(host(), link))
                .show(anchor);
    }

    private void joinServer(LibraryData.Game g) {
        LinearLayout box = new LinearLayout(requireContext());
        box.setOrientation(LinearLayout.VERTICAL);
        int pad = Ui.dp(requireContext(), 20);
        box.setPadding(pad, Ui.dp(requireContext(), 8), pad, 0);
        TextInputLayout linkLayout = new TextInputLayout(requireContext());
        linkLayout.setHint(getString(R.string.library_link_hint));
        TextInputEditText link = new TextInputEditText(linkLayout.getContext());
        link.setSingleLine(true);
        linkLayout.addView(link);
        TextInputLayout instanceLayout = new TextInputLayout(requireContext());
        instanceLayout.setHint(getString(R.string.library_instance_hint));
        TextInputEditText instance = new TextInputEditText(instanceLayout.getContext());
        instance.setSingleLine(true);
        instanceLayout.addView(instance);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Ui.dp(requireContext(), 8);
        box.addView(linkLayout);
        box.addView(instanceLayout, lp);
        AlertDialog d = Ui.alert(requireContext())
                .setTitle(R.string.library_join_server)
                .setView(box)
                .setPositiveButton(R.string.library_play, null)
                .setNegativeButton(R.string.common_cancel, null)
                .show();
        d.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(x -> {
            String code = link.getText() == null ? "" : link.getText().toString().trim();
            String inst = instance.getText() == null ? "" : instance.getText().toString().trim();
            linkLayout.setError(null);
            instanceLayout.setError(null);
            if (!code.isEmpty() && !inst.isEmpty()) {
                instanceLayout.setError(getString(R.string.library_exclusive_link));
                return;
            }
            Deeplink dl = Deeplink.place(g.placeId, inst, code);
            if (dl == null) {
                if (!code.isEmpty()) linkLayout.setError(getString(R.string.library_invalid_link));
                else instanceLayout.setError(getString(R.string.library_invalid_instance));
                return;
            }
            d.dismiss();
            Actions.launch(host(), dl, g.title());
        });
    }

    private void addFromText() {
        String text = add.getText() == null ? "" : add.getText().toString().trim();
        if (text.isEmpty()) return;
        Deeplink d = Deeplink.findIn(text);
        if (d == null) {
            showStatus(R.string.library_add_invalid);
            return;
        }
        if (!d.isPlace()) {
            add.setText("");
            showStatus(0);
            Actions.launch(host(), d, getString(R.string.library_share_link));
            return;
        }
        showStatus(0);
        View button = getView() == null ? null : getView().findViewById(R.id.lib_add_button);
        if (button != null) button.setEnabled(false);
        Net.meta(requireContext(), d.placeId, m -> {
            if (button != null) button.setEnabled(true);
            if (getView() == null) return;
            if (m == null) {
                showStatus(R.string.library_add_failed);
                return;
            }
            add.setText("");
            String name = m.name == null ? "" : Store.clip(m.name, 200);
            Store.Game existing = store.game(d.placeId);
            if (existing == null) store.pin(new Store.Game(d.placeId, m.universeId, name, m.iconUrl == null ? "" : m.iconUrl, System.currentTimeMillis()));
            LibraryData.Game g = find(d.placeId);
            Notify.say(host(), Notify.LIBRARY, getString(R.string.library_added, name.isEmpty() ? String.valueOf(d.placeId) : name));
            if (g != null) select(g);
        });
    }

    private void showStatus(int text) {
        status.setVisibility(text == 0 ? View.GONE : View.VISIBLE);
        if (text != 0) status.setText(text);
    }

    private String lastPlayed(long time) {
        if (time <= 0) return getString(R.string.library_never);
        if (DateUtils.isToday(time)) return getString(R.string.library_today);
        if (DateUtils.isToday(time + DAY)) return getString(R.string.library_yesterday);
        return DateFormat.getDateInstance(DateFormat.MEDIUM).format(time);
    }

    private String updatedAgo(long time) {
        if (time <= 0) return "";
        long days = (System.currentTimeMillis() - time) / DAY;
        if (days < 1) return getString(R.string.library_updated_today);
        if (days < 2) return getString(R.string.library_updated_yesterday);
        if (days < 30) return getResources().getQuantityString(R.plurals.library_updated_days, (int) days, (int) days);
        return getString(R.string.library_updated_on, DateFormat.getDateInstance(DateFormat.MEDIUM).format(time));
    }

    private void showPin(TextView title, boolean pinned) {
        android.graphics.drawable.Drawable pin = null;
        if (pinned) {
            pin = androidx.core.content.ContextCompat.getDrawable(requireContext(), R.drawable.ic_pin);
            if (pin != null) {
                pin = pin.mutate();
                int size = Ui.dp(requireContext(), 11);
                pin.setBounds(0, 0, size, size);
            }
        }
        title.setCompoundDrawablesRelative(null, null, pin, null);
    }

    private final class SideAdapter extends RecyclerView.Adapter<RecyclerView.ViewHolder> {
        private List<Object> rows = new ArrayList<>();
        private List<String> keys = new ArrayList<>();
        private int pinnedCount;
        private int allCount;
        private long boundSelected;

        void set(List<Object> next, int pinned, int all) {
            List<Object> prev = rows;
            List<String> prevKeys = keys;
            List<String> nextKeys = new ArrayList<>(next.size());
            for (Object r : next) nextKeys.add(r instanceof LibraryData.Game ? key((LibraryData.Game) r) : "");
            int oldPinned = pinnedCount;
            int oldAll = allCount;
            long oldSel = boundSelected;
            long newSel = selected == null ? 0 : selected.placeId;
            androidx.recyclerview.widget.DiffUtil.DiffResult diff = androidx.recyclerview.widget.DiffUtil.calculateDiff(new androidx.recyclerview.widget.DiffUtil.Callback() {
                @Override
                public int getOldListSize() {
                    return prev.size();
                }

                @Override
                public int getNewListSize() {
                    return next.size();
                }

                @Override
                public boolean areItemsTheSame(int o, int n) {
                    Object a = prev.get(o);
                    Object b = next.get(n);
                    if (a instanceof Boolean && b instanceof Boolean) return a.equals(b);
                    return a instanceof LibraryData.Game && b instanceof LibraryData.Game && ((LibraryData.Game) a).placeId == ((LibraryData.Game) b).placeId;
                }

                @Override
                public boolean areContentsTheSame(int o, int n) {
                    Object a = prev.get(o);
                    Object b = next.get(n);
                    if (a instanceof Boolean) return (Boolean) a ? oldPinned == pinned : oldAll == all;
                    LibraryData.Game ga = (LibraryData.Game) a;
                    LibraryData.Game gb = (LibraryData.Game) b;
                    return prevKeys.get(o).equals(nextKeys.get(n)) && (ga.placeId == oldSel) == (gb.placeId == newSel);
                }
            });
            rows = next;
            keys = nextKeys;
            pinnedCount = pinned;
            allCount = all;
            boundSelected = newSel;
            diff.dispatchUpdatesTo(this);
        }

        private String key(LibraryData.Game g) {
            return g.pinned + "|" + g.title() + "|" + g.iconUrl;
        }

        @Override
        public int getItemViewType(int position) {
            return rows.get(position) instanceof Boolean ? 0 : 1;
        }

        @Override
        public int getItemCount() {
            return rows.size();
        }

        @NonNull
        @Override
        public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int type) {
            View v = LayoutInflater.from(parent.getContext()).inflate(type == 0 ? R.layout.item_library_group : R.layout.item_library_side, parent, false);
            return new RecyclerView.ViewHolder(v) {
            };
        }

        @Override
        public void onBindViewHolder(@NonNull RecyclerView.ViewHolder h, int position) {
            Object row = rows.get(position);
            View v = h.itemView;
            if (row instanceof Boolean) {
                boolean pinned = (Boolean) row;
                boolean open = pinned ? pinnedOpen : allOpen;
                ((TextView) v.findViewById(R.id.group_name)).setText(pinned ? R.string.library_group_pinned : R.string.library_group_all);
                ((TextView) v.findViewById(R.id.group_count)).setText(getString(R.string.library_group_count, pinned ? pinnedCount : allCount));
                ImageView chevron = v.findViewById(R.id.group_chevron);
                chevron.setRotation(open ? 0f : -90f);
                v.setOnClickListener(x -> {
                    if (pinned) pinnedOpen = !pinnedOpen;
                    else allOpen = !allOpen;
                    chevron.animate().rotation((pinned ? pinnedOpen : allOpen) ? 0f : -90f).setDuration(200).start();
                    v.postDelayed(LibraryFragment.this::render, 120);
                });
                return;
            }
            LibraryData.Game g = (LibraryData.Game) row;
            Net.image(v.findViewById(R.id.side_icon), g.iconUrl, 0, Ui.dp(requireContext(), 18));
            ((TextView) v.findViewById(R.id.side_name)).setText(g.title());
            v.findViewById(R.id.side_pin).setVisibility(g.pinned ? View.VISIBLE : View.GONE);
            v.setSelected(selected != null && selected.placeId == g.placeId);
            v.setOnClickListener(x -> select(g));
            Flyout.bind(v, () -> gameMenu(g));
            v.setContentDescription(g.title());
        }
    }

    static final class Watcher implements android.text.TextWatcher {
        private final Runnable r;

        Watcher(Runnable r) {
            this.r = r;
        }

        @Override
        public void beforeTextChanged(CharSequence s, int a, int b, int c) {
        }

        @Override
        public void onTextChanged(CharSequence s, int a, int b, int c) {
        }

        @Override
        public void afterTextChanged(android.text.Editable s) {
            r.run();
        }
    }
}
