#include <windows.h>
#include <reshade.hpp>
#include "remap_comp.h"
#include "resolve_comp.h"
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>

using namespace reshade::api;

extern "C" __declspec(dllexport) const char *NAME = "MuMu Depth";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Copies the game depth buffer from MuMu's guest Vulkan device to the device that presents the window.";

namespace
{
    struct depth_stats
    {
        uint32_t draws = 0;
        uint32_t last_frame = 0;
        uint32_t width = 0;
        uint32_t height = 0;
    };

    struct source_state
    {
        std::unordered_map<uint64_t, depth_stats> stats;
        uint64_t best = 0;
        uint64_t challenger = 0;
        uint32_t challenger_wins = 0;
        resource shared = {};
        resource_view shared_view = {};
        uint32_t shared_width = 0;
        uint32_t shared_height = 0;
        pipeline_layout layout = {};
        pipeline resolve = {};
        sampler point = {};
        descriptor_table table = {};
        uint64_t table_source = 0;
        std::unordered_map<uint64_t, resource_view> views;
    };

    struct command_state
    {
        uint64_t dsv_resource = 0;
    };

    struct presenter_state
    {
        resource imported = {};
        resource_view srv = {};
        uint64_t generation = 0;
        resource target = {};
        resource_view target_uav = {};
        resource_view target_srv = {};
        uint32_t target_width = 0;
        uint32_t target_height = 0;
        pipeline_layout layout = {};
        pipeline remap = {};
        sampler point = {};
        descriptor_table table = {};
        resource_view table_view = {};
        bool bound = false;
    };



    std::mutex lock;
    std::unordered_set<device *> presenters;
    std::unordered_map<device *, source_state> sources;
    std::unordered_map<command_list *, command_state> commands;
    std::unordered_map<effect_runtime *, presenter_state> runtimes;

    HANDLE shared_handle = nullptr;
    resource_desc published_desc = {};
    uint32_t published_width = 0;
    volatile ULONGLONG last_copy = 0;
    uint32_t published_height = 0;
    uint64_t generation = 0;

    void log(const std::string &text)
    {
        reshade::log::message(reshade::log::level::info, text.c_str());
    }

    DWORD guarded_code = 0;
    void *guarded_address = nullptr;

    int capture(EXCEPTION_POINTERS *info)
    {
        guarded_code = info->ExceptionRecord->ExceptionCode;
        guarded_address = info->ExceptionRecord->ExceptionAddress;
        return EXCEPTION_EXECUTE_HANDLER;
    }

    bool guarded_create_pipeline(device *dev, pipeline_layout layout, const pipeline_subobject *subobject, pipeline *out)
    {
        __try
        {
            return dev->create_pipeline(layout, 1, subobject, out);
        }
        __except (capture(GetExceptionInformation()))
        {
            return false;
        }
    }

    format view_format(format f)
    {
        switch (f)
        {
        case format::d16_unorm:
            return format::r16_unorm;
        case format::d24_unorm_s8_uint:
        case format::d24_unorm_x8_uint:
            return format::r24_unorm_x8_uint;
        case format::d32_float:
            return format::r32_float;
        case format::d32_float_s8_uint:
            return format::r32_float_x8_uint;
        default:
            return format_to_default_typed(f);
        }
    }

    bool is_depth(format f)
    {
        return f == format::d16_unorm || f == format::d24_unorm_s8_uint || f == format::d24_unorm_x8_uint ||
               f == format::d32_float || f == format::d32_float_s8_uint;
    }

    void release_shared(device *dev, source_state &s)
    {
        if (s.shared_view.handle != 0)
            dev->destroy_resource_view(s.shared_view);
        s.shared_view = {};
        s.table_source = 0;
        if (s.shared.handle != 0)
            dev->destroy_resource(s.shared);
        s.shared = {};
        s.shared_width = 0;
        s.shared_height = 0;
    }

    bool ensure_shared(device *dev, source_state &s, const resource_desc &src)
    {
        if (src.texture.format != format::d32_float && src.texture.format != format::d32_float_s8_uint)
            return false;
        if (s.shared.handle != 0 && s.shared_width == src.texture.width && s.shared_height == src.texture.height)
            return true;
        release_shared(dev, s);
        if (!dev->check_capability(device_caps::shared_resource_nt_handle))
            return false;
        const uint64_t bytes = uint64_t(src.texture.width) * src.texture.height * 4;
        resource_desc desc(bytes, memory_heap::default_, resource_usage::unordered_access,
                           resource_flags::shared | resource_flags::shared_nt_handle);
        HANDLE handle = nullptr;
        if (!dev->create_resource(desc, nullptr, resource_usage::unordered_access, &s.shared, &handle) || handle == nullptr)
        {
            log("could not create shared depth buffer");
            s.shared = {};
            return false;
        }
        s.shared_width = src.texture.width;
        s.shared_height = src.texture.height;
        shared_handle = handle;
        published_desc = desc;
        published_width = s.shared_width;
        published_height = s.shared_height;
        generation++;
        log("shared depth buffer " + std::to_string(s.shared_width) + "x" + std::to_string(s.shared_height) + " samples " +
            std::to_string(src.texture.samples) + " format " + std::to_string(static_cast<uint32_t>(src.texture.format)));
        return true;
    }

    bool ensure_resolve(device *dev, source_state &s)
    {
        if (s.resolve.handle != 0)
            return true;
        if (s.layout.handle != 0)
            return false;
        descriptor_range ranges[2] = {};
        ranges[0].binding = 0;
        ranges[0].count = 1;
        ranges[0].visibility = shader_stage::compute;
        ranges[0].type = descriptor_type::sampler_with_resource_view;
        ranges[1].binding = 1;
        ranges[1].count = 1;
        ranges[1].visibility = shader_stage::compute;
        ranges[1].type = descriptor_type::buffer_unordered_access_view;
        constant_range constants = {};
        constants.count = 4;
        constants.visibility = shader_stage::compute;
        pipeline_layout_param params[2] = { pipeline_layout_param(2, ranges), pipeline_layout_param(constants) };
        if (!dev->create_pipeline_layout(2, params, &s.layout))
        {
            log("could not create depth resolve layout");
            return false;
        }
        shader_desc cs = {};
        cs.code = resolve_comp;
        cs.code_size = sizeof(resolve_comp);
        cs.entry_point = "main";
        pipeline_subobject subobject = { pipeline_subobject_type::compute_shader, 1, &cs };
        sampler_desc sd = {};
        sd.filter = filter_mode::min_mag_mip_point;
        if (!dev->create_pipeline(s.layout, 1, &subobject, &s.resolve) || !dev->create_sampler(sd, &s.point) ||
            !dev->allocate_descriptor_table(s.layout, 0, &s.table))
        {
            log("could not create depth resolve pipeline");
            return false;
        }
        log("depth resolve pipeline ready");
        return true;
    }

    resource_view depth_view(device *dev, source_state &s, resource src, const resource_desc &desc)
    {
        auto it = s.views.find(src.handle);
        if (it != s.views.end())
            return it->second;
        resource_view view = {};
        const resource_view_type type = desc.texture.samples > 1 ? resource_view_type::texture_2d_multisample : resource_view_type::texture_2d;
        if (!dev->create_resource_view(src, resource_usage::shader_resource, resource_view_desc(type, view_format(desc.texture.format), 0, 1, 0, 1), &view))
            view = {};
        s.views[src.handle] = view;
        return view;
    }

    void copy_depth(command_list *cmd, device *dev, source_state &s, resource src)
    {
        const resource_desc desc = dev->get_resource_desc(src);
        if ((desc.usage & resource_usage::shader_resource) == 0 || !ensure_shared(dev, s, desc) || !ensure_resolve(dev, s))
            return;
        if (s.shared_view.handle == 0 &&
            !dev->create_resource_view(s.shared, resource_usage::unordered_access,
                                       resource_view_desc(format::r32_float, 0, published_desc.buffer.size), &s.shared_view))
            return;
        const resource_view view = depth_view(dev, s, src, desc);
        if (view.handle == 0)
            return;
        if (s.table_source != src.handle)
        {
            const sampler_with_resource_view tex = { s.point, view };
            const descriptor_table_update updates[2] = {
                { s.table, 0, 0, 1, descriptor_type::sampler_with_resource_view, &tex },
                { s.table, 1, 0, 1, descriptor_type::buffer_unordered_access_view, &s.shared_view },
            };
            dev->update_descriptor_tables(2, updates);
            s.table_source = src.handle;
        }
        const uint32_t constants[4] = { desc.texture.width, desc.texture.height, desc.texture.samples, 0 };
        cmd->barrier(src, resource_usage::depth_stencil_write, resource_usage::shader_resource);
        cmd->bind_pipeline(pipeline_stage::all_compute, s.resolve);
        cmd->bind_descriptor_table(shader_stage::compute, s.layout, 0, s.table);
        cmd->push_constants(shader_stage::compute, s.layout, 1, 0, 4, constants);
        cmd->dispatch((desc.texture.width + 7) / 8, (desc.texture.height + 7) / 8, 1);
        cmd->barrier(src, resource_usage::shader_resource, resource_usage::depth_stencil_write);
        last_copy = GetTickCount64();
    }

    bool on_create_resource(device *dev, resource_desc &desc, subresource_data *, resource_usage)
    {
        if (desc.type != resource_type::texture_2d || !is_depth(desc.texture.format) ||
            (desc.usage & resource_usage::depth_stencil) == 0)
            return false;
        {
            std::lock_guard guard(lock);
            if (presenters.contains(dev))
                return false;
        }
        if ((desc.usage & resource_usage::shader_resource) != 0)
            return false;
        desc.usage |= resource_usage::shader_resource;
        return true;
    }

    void on_depth_cleared(command_list *cmd, resource_view dsv)
    {
        device *dev = cmd->get_device();
        std::lock_guard guard(lock);
        if (presenters.contains(dev))
            return;
        source_state &s = sources[dev];
        const resource res = dev->get_resource_from_view(dsv);
        if (res.handle == 0)
            return;
        depth_stats &st = s.stats[res.handle];
        if (st.width == 0)
        {
            const resource_desc desc = dev->get_resource_desc(res);
            st.width = desc.texture.width;
            st.height = desc.texture.height;
        }
        st.last_frame = st.draws;
        st.draws = 0;
        uint64_t leader = 0;
        uint64_t most = 0;
        for (const auto &[handle, value] : s.stats)
        {
            const uint64_t score = value.width == value.height ? value.last_frame / 4 : value.last_frame;
            if (score > most)
            {
                most = score;
                leader = handle;
            }
        }
        if (s.best == 0 || !s.stats.contains(s.best))
        {
            s.best = leader;
        }
        else if (leader != s.best)
        {
            if (leader == s.challenger)
                s.challenger_wins++;
            else
            {
                s.challenger = leader;
                s.challenger_wins = 1;
            }
            if (s.challenger_wins > 120)
            {
                s.best = leader;
                s.challenger = 0;
                s.challenger_wins = 0;
            }
        }
        else
        {
            s.challenger = 0;
            s.challenger_wins = 0;
        }
        if (res.handle == s.best && st.last_frame > 0)
            copy_depth(cmd, dev, s, res);
    }

    void on_init_swapchain(swapchain *chain, bool)
    {
        std::lock_guard guard(lock);
        presenters.insert(chain->get_device());
    }

    void on_destroy_device(device *dev)
    {
        std::lock_guard guard(lock);
        auto it = sources.find(dev);
        if (it != sources.end())
        {
            for (auto &[handle, view] : it->second.views)
                if (view.handle != 0)
                    dev->destroy_resource_view(view);
            release_shared(dev, it->second);
            sources.erase(it);
        }
        presenters.erase(dev);
    }

    void on_destroy_command_list(command_list *cmd)
    {
        std::lock_guard guard(lock);
        commands.erase(cmd);
    }

    void set_dsv(command_list *cmd, resource_view dsv)
    {
        device *dev = cmd->get_device();
        std::lock_guard guard(lock);
        commands[cmd].dsv_resource = dsv.handle != 0 ? dev->get_resource_from_view(dsv).handle : 0;
    }

    bool on_begin_render_pass(command_list *cmd, uint32_t, const render_pass_render_target_desc *,
                              const render_pass_depth_stencil_desc *ds, render_pass_flags)
    {
        if (ds != nullptr && ds->view.handle != 0)
        {
            const resource_desc desc = cmd->get_device()->get_resource_desc(cmd->get_device()->get_resource_from_view(ds->view));
            if (is_depth(desc.texture.format) && ds->depth_load_op == render_pass_load_op::clear)
                on_depth_cleared(cmd, ds->view);
            set_dsv(cmd, is_depth(desc.texture.format) ? ds->view : resource_view{});
        }
        else
        {
            set_dsv(cmd, {});
        }
        return false;
    }

    bool on_end_render_pass(command_list *cmd)
    {
        set_dsv(cmd, {});
        return false;
    }

    void on_bind_targets(command_list *cmd, uint32_t, const resource_view *, resource_view dsv)
    {
        set_dsv(cmd, dsv);
    }

    bool on_clear_dsv(command_list *cmd, resource_view dsv, const float *depth, const uint8_t *, uint32_t, const rect *)
    {
        if (depth != nullptr)
            on_depth_cleared(cmd, dsv);
        return false;
    }

    void count_draw(command_list *cmd)
    {
        device *dev = cmd->get_device();
        std::lock_guard guard(lock);
        if (presenters.contains(dev))
            return;
        auto it = commands.find(cmd);
        if (it == commands.end() || it->second.dsv_resource == 0)
            return;
        sources[dev].stats[it->second.dsv_resource].draws++;
    }

    bool on_draw(command_list *cmd, uint32_t, uint32_t, uint32_t, uint32_t)
    {
        count_draw(cmd);
        return false;
    }

    bool on_draw_indexed(command_list *cmd, uint32_t, uint32_t, uint32_t, int32_t, uint32_t)
    {
        count_draw(cmd);
        return false;
    }

    int rotation_for(effect_runtime *runtime)
    {
        int rotation = 2;
        reshade::get_config_value(runtime, "MUMU_DEPTH", "Rotation", rotation);
        return rotation;
    }

    void release_import(effect_runtime *runtime, presenter_state &p)
    {
        device *dev = runtime->get_device();
        if (p.srv.handle != 0)
            dev->destroy_resource_view(p.srv);
        if (p.imported.handle != 0)
            dev->destroy_resource(p.imported);
        p.srv = {};
        p.imported = {};
    }

    void release_target(device *dev, presenter_state &p)
    {
        if (p.target_uav.handle != 0)
            dev->destroy_resource_view(p.target_uav);
        if (p.target_srv.handle != 0)
            dev->destroy_resource_view(p.target_srv);
        if (p.target.handle != 0)
            dev->destroy_resource(p.target);
        p.target = {};
        p.target_uav = {};
        p.target_srv = {};
        p.target_width = 0;
        p.target_height = 0;
        p.bound = false;
        p.table_view = {};
    }

    void release_pipeline(device *dev, presenter_state &p)
    {
        if (p.table.handle != 0)
            dev->free_descriptor_table(p.table);
        p.table = {};
        p.table_view = {};
        if (p.remap.handle != 0)
            dev->destroy_pipeline(p.remap);
        if (p.layout.handle != 0)
            dev->destroy_pipeline_layout(p.layout);
        if (p.point.handle != 0)
            dev->destroy_sampler(p.point);
        p.remap = {};
        p.layout = {};
        p.point = {};
    }

    bool ensure_pipeline(device *dev, presenter_state &p)
    {
        if (p.remap.handle != 0)
            return true;
        pipeline_layout_param params[2];
        constant_range constants = {};
        constants.count = 8;
        constants.visibility = shader_stage::compute;
        params[1] = pipeline_layout_param(constants);
        descriptor_range ranges[2] = {};
        ranges[0].binding = 0;
        ranges[0].count = 1;
        ranges[0].visibility = shader_stage::compute;
        ranges[0].type = descriptor_type::buffer_unordered_access_view;
        ranges[1].binding = 1;
        ranges[1].count = 1;
        ranges[1].visibility = shader_stage::compute;
        ranges[1].type = descriptor_type::unordered_access_view;
        params[0] = pipeline_layout_param(2, ranges);
        if (!dev->create_pipeline_layout(2, params, &p.layout))
        {
            log("could not create depth remap layout");
            return false;
        }
        shader_desc cs = {};
        cs.code = remap_comp;
        cs.code_size = sizeof(remap_comp);
        cs.entry_point = "main";
        pipeline_subobject subobject = { pipeline_subobject_type::compute_shader, 1, &cs };
        if (!guarded_create_pipeline(dev, p.layout, &subobject, &p.remap))
        {
            HMODULE mod = nullptr;
            wchar_t name[MAX_PATH] = {};
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               static_cast<LPCWSTR>(guarded_address), &mod);
            GetModuleFileNameW(mod, name, MAX_PATH);
            char narrow[MAX_PATH] = {};
            WideCharToMultiByte(CP_UTF8, 0, name, -1, narrow, MAX_PATH, nullptr, nullptr);
            log("pipeline exception " + std::to_string(guarded_code) + " in " + narrow + " +" +
                std::to_string(reinterpret_cast<uintptr_t>(guarded_address) - reinterpret_cast<uintptr_t>(mod)));
            log("could not create depth remap pipeline");
            release_pipeline(dev, p);
            return false;
        }
        sampler_desc sd = {};
        sd.filter = filter_mode::min_mag_mip_point;
        sd.address_u = texture_address_mode::clamp;
        sd.address_v = texture_address_mode::clamp;
        sd.address_w = texture_address_mode::clamp;
        if (!dev->allocate_descriptor_table(p.layout, 0, &p.table))
        {
            release_pipeline(dev, p);
            return false;
        }
        return true;
    }

    bool ensure_target(device *dev, presenter_state &p, uint32_t width, uint32_t height)
    {
        if (p.target.handle != 0 && p.target_width == width && p.target_height == height)
            return true;
        release_target(dev, p);
        resource_desc desc(width, height, 1, 1, format::r32_float, 1, memory_heap::default_,
                           resource_usage::unordered_access | resource_usage::shader_resource);
        if (!dev->create_resource(desc, nullptr, resource_usage::shader_resource, &p.target))
        {
            log("could not create aligned depth texture");
            return false;
        }
        log("aligned depth texture " + std::to_string(width) + "x" + std::to_string(height));
        if (!dev->create_resource_view(p.target, resource_usage::unordered_access, resource_view_desc(format::r32_float), &p.target_uav) ||
            !dev->create_resource_view(p.target, resource_usage::shader_resource, resource_view_desc(format::r32_float), &p.target_srv))
        {
            release_target(dev, p);
            return false;
        }
        p.target_width = width;
        p.target_height = height;
        return true;
    }

    bool import_shared(effect_runtime *runtime, presenter_state &p)
    {
        if (p.generation == generation && p.srv.handle != 0)
            return true;
        release_import(runtime, p);
        p.generation = generation;
        device *dev = runtime->get_device();
        HANDLE handle = shared_handle;
        if (!dev->create_resource(published_desc, nullptr, resource_usage::unordered_access, &p.imported, &handle))
        {
            log("could not import shared depth image on the presenting device");
            return false;
        }
        if (!dev->create_resource_view(p.imported, resource_usage::unordered_access,
                                       resource_view_desc(format::r32_float, 0, published_desc.buffer.size), &p.srv))
        {
            log("could not create depth view on the presenting device");
            release_import(runtime, p);
            return false;
        }
        log("imported shared depth image");
        return true;
    }

    void on_begin_effects(effect_runtime *runtime, command_list *cmd, resource_view, resource_view)
    {
        std::lock_guard guard(lock);
        if (shared_handle == nullptr)
            return;
        presenter_state &p = runtimes[runtime];
        device *dev = runtime->get_device();
        uint32_t width = 0, height = 0;
        runtime->get_screenshot_width_and_height(&width, &height);
        if (width == 0 || height == 0)
            return;
        if (!import_shared(runtime, p))
            return;
        if (p.remap.handle == 0)
            return;
        if (!ensure_target(dev, p, width, height))
            return;

        int rotation = rotation_for(runtime);
        int flip_x = 0, flip_y = 0;
        float far_value = 0.0f;
        reshade::get_config_value(runtime, "MUMU_DEPTH", "FlipX", flip_x);
        reshade::get_config_value(runtime, "MUMU_DEPTH", "FlipY", flip_y);
        reshade::get_config_value(runtime, "MUMU_DEPTH", "FarValue", far_value);
        const float constants[8] = {
            0.0f, 0.0f, 1.0f, 1.0f,
            float(rotation) + float(flip_x) * 4.0f + float(flip_y) * 8.0f, float(published_width), float(published_height), far_value,
        };

        if (p.table_view != p.srv)
        {
            const descriptor_table_update updates[2] = {
                { p.table, 0, 0, 1, descriptor_type::buffer_unordered_access_view, &p.srv },
                { p.table, 1, 0, 1, descriptor_type::unordered_access_view, &p.target_uav },
            };
            dev->update_descriptor_tables(2, updates);
            p.table_view = p.srv;
        }
        cmd->barrier(p.target, resource_usage::shader_resource, resource_usage::unordered_access);
        cmd->bind_pipeline(pipeline_stage::all_compute, p.remap);
        cmd->push_constants(shader_stage::compute, p.layout, 1, 0, 8, constants);
        cmd->bind_descriptor_table(shader_stage::compute, p.layout, 0, p.table);
        cmd->dispatch((width + 7) / 8, (height + 7) / 8, 1);
        cmd->barrier(p.target, resource_usage::unordered_access, resource_usage::shader_resource);

        if (!p.bound)
        {
            runtime->update_texture_bindings("DEPTH", p.target_srv, p.target_srv);
            p.bound = true;
            log("aligned depth bound to effects");
        }
    }

    void on_present(effect_runtime *runtime)
    {
        const bool live = shared_handle != nullptr && GetTickCount64() - last_copy < 500;
        if (runtime->get_effects_state() != live)
            runtime->set_effects_state(live);
    }

    void on_init_runtime(effect_runtime *runtime)
    {
        std::lock_guard guard(lock);
        presenter_state &p = runtimes[runtime];
        log(ensure_pipeline(runtime->get_device(), p) ? "depth remap pipeline ready" : "depth remap pipeline failed");
    }

    void on_destroy_runtime(effect_runtime *runtime)
    {
        std::lock_guard guard(lock);
        auto it = runtimes.find(runtime);
        if (it == runtimes.end())
            return;
        release_import(runtime, it->second);
        release_target(runtime->get_device(), it->second);
        release_pipeline(runtime->get_device(), it->second);
        runtimes.erase(it);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        if (!reshade::register_addon(module))
            return FALSE;
        reshade::register_event<reshade::addon_event::init_swapchain>(on_init_swapchain);
        reshade::register_event<reshade::addon_event::create_resource>(on_create_resource);
        reshade::register_event<reshade::addon_event::destroy_device>(on_destroy_device);
        reshade::register_event<reshade::addon_event::destroy_command_list>(on_destroy_command_list);
        reshade::register_event<reshade::addon_event::begin_render_pass>(on_begin_render_pass);
        reshade::register_event<reshade::addon_event::end_render_pass>(on_end_render_pass);
        reshade::register_event<reshade::addon_event::bind_render_targets_and_depth_stencil>(on_bind_targets);
        reshade::register_event<reshade::addon_event::clear_depth_stencil_view>(on_clear_dsv);
        reshade::register_event<reshade::addon_event::draw>(on_draw);
        reshade::register_event<reshade::addon_event::draw_indexed>(on_draw_indexed);
        reshade::register_event<reshade::addon_event::reshade_begin_effects>(on_begin_effects);
        reshade::register_event<reshade::addon_event::init_effect_runtime>(on_init_runtime);
        reshade::register_event<reshade::addon_event::reshade_present>(on_present);
        reshade::register_event<reshade::addon_event::destroy_effect_runtime>(on_destroy_runtime);
        break;
    case DLL_PROCESS_DETACH:
        reshade::unregister_addon(module);
        break;
    }
    return TRUE;
}
