#include <fcntl.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>
#include "shaders.h"

#define LAYER_NAME "VK_LAYER_VOIDSTRAP_overlay"
#define SHM_MAGIC 0x564f5356u
#define SHM_VERSION 1u
#define SHM_HEADER 64u
#define MAX_EDGE 4096u
#define MAX_ITEMS 64
#define EXPORT __attribute__((visibility("default")))

typedef struct {
	uint32_t magic;
	uint32_t version;
	volatile uint32_t sequence;
	volatile uint32_t slot;
	uint32_t width;
	uint32_t height;
	uint32_t visible;
	volatile uint32_t capture_wanted;
	volatile uint64_t presented;
	volatile uint64_t present_ns;
	volatile uint32_t swap_width;
	volatile uint32_t swap_height;
	volatile uint32_t capture_sequence;
	volatile uint32_t capture_format;
} shm_header;

typedef struct {
	void *key;
	VkInstance instance;
	PFN_vkGetInstanceProcAddr gipa;
	PFN_vkDestroyInstance destroy;
	PFN_vkGetPhysicalDeviceMemoryProperties memory_properties;
	PFN_vkGetPhysicalDeviceQueueFamilyProperties queue_families;
} instance_entry;

typedef struct {
	VkImage image;
	VkImageView view;
	VkFramebuffer framebuffer;
	VkCommandBuffer commands;
	VkFence fence;
	VkSemaphore finished;
	int submitted;
	int captured;
} frame_entry;

typedef struct swapchain_entry swapchain_entry;

typedef struct {
	void *key;
	VkDevice device;
	VkPhysicalDevice physical;
	PFN_vkGetDeviceProcAddr gdpa;
	PFN_vkSetDeviceLoaderData set_loader_data;
	VkQueue queues[16];
	uint32_t queue_families[16];
	uint32_t queue_count;
	VkPhysicalDeviceMemoryProperties memory;
	uint32_t graphics_family;
	int has_graphics;
	VkQueue graphics_queue;
	PFN_vkDestroyDevice destroy_device;
	PFN_vkGetDeviceQueue get_queue;
	PFN_vkGetDeviceQueue2 get_queue2;
	PFN_vkCreateSwapchainKHR create_swapchain;
	PFN_vkDestroySwapchainKHR destroy_swapchain;
	PFN_vkGetSwapchainImagesKHR get_images;
	PFN_vkQueuePresentKHR present;
	PFN_vkQueueSubmit submit;
	PFN_vkCreateImageView create_view;
	PFN_vkDestroyImageView destroy_view;
	PFN_vkCreateRenderPass create_render_pass;
	PFN_vkDestroyRenderPass destroy_render_pass;
	PFN_vkCreateFramebuffer create_framebuffer;
	PFN_vkDestroyFramebuffer destroy_framebuffer;
	PFN_vkCreateShaderModule create_shader;
	PFN_vkDestroyShaderModule destroy_shader;
	PFN_vkCreatePipelineLayout create_pipeline_layout;
	PFN_vkDestroyPipelineLayout destroy_pipeline_layout;
	PFN_vkCreateGraphicsPipelines create_pipelines;
	PFN_vkDestroyPipeline destroy_pipeline;
	PFN_vkCreateDescriptorSetLayout create_set_layout;
	PFN_vkDestroyDescriptorSetLayout destroy_set_layout;
	PFN_vkCreateDescriptorPool create_descriptor_pool;
	PFN_vkDestroyDescriptorPool destroy_descriptor_pool;
	PFN_vkAllocateDescriptorSets allocate_sets;
	PFN_vkUpdateDescriptorSets update_sets;
	PFN_vkCreateSampler create_sampler;
	PFN_vkDestroySampler destroy_sampler;
	PFN_vkCreateCommandPool create_command_pool;
	PFN_vkDestroyCommandPool destroy_command_pool;
	PFN_vkAllocateCommandBuffers allocate_commands;
	PFN_vkBeginCommandBuffer begin_commands;
	PFN_vkEndCommandBuffer end_commands;
	PFN_vkResetCommandBuffer reset_commands;
	PFN_vkCmdBeginRenderPass cmd_begin_pass;
	PFN_vkCmdEndRenderPass cmd_end_pass;
	PFN_vkCmdBindPipeline cmd_bind_pipeline;
	PFN_vkCmdBindDescriptorSets cmd_bind_sets;
	PFN_vkCmdDraw cmd_draw;
	PFN_vkCmdSetViewport cmd_viewport;
	PFN_vkCmdSetScissor cmd_scissor;
	PFN_vkCmdPipelineBarrier cmd_barrier;
	PFN_vkCmdCopyBufferToImage cmd_copy;
	PFN_vkCmdCopyImageToBuffer cmd_copy_out;
	PFN_vkGetFenceStatus fence_status;
	PFN_vkCreateFence create_fence;
	PFN_vkDestroyFence destroy_fence;
	PFN_vkWaitForFences wait_fences;
	PFN_vkResetFences reset_fences;
	PFN_vkCreateSemaphore create_semaphore;
	PFN_vkDestroySemaphore destroy_semaphore;
	PFN_vkCreateImage create_image;
	PFN_vkDestroyImage destroy_image;
	PFN_vkCreateBuffer create_buffer;
	PFN_vkDestroyBuffer destroy_buffer;
	PFN_vkGetImageMemoryRequirements image_requirements;
	PFN_vkGetBufferMemoryRequirements buffer_requirements;
	PFN_vkAllocateMemory allocate_memory;
	PFN_vkFreeMemory free_memory;
	PFN_vkBindImageMemory bind_image;
	PFN_vkBindBufferMemory bind_buffer;
	PFN_vkMapMemory map_memory;
	PFN_vkDeviceWaitIdle wait_idle;
	swapchain_entry *swapchains;
} device_entry;

struct swapchain_entry {
	VkSwapchainKHR handle;
	device_entry *device;
	VkFormat format;
	VkExtent2D extent;
	uint32_t count;
	frame_entry *frames;
	VkRenderPass render_pass;
	VkPipelineLayout pipeline_layout;
	VkPipeline pipeline;
	VkDescriptorSetLayout set_layout;
	VkDescriptorPool descriptor_pool;
	VkDescriptorSet descriptor_set;
	VkSampler sampler;
	VkCommandPool command_pool;
	VkImage texture;
	VkDeviceMemory texture_memory;
	VkImageView texture_view;
	VkBuffer staging;
	VkDeviceMemory staging_memory;
	void *staging_mapped;
	uint32_t texture_width;
	uint32_t texture_height;
	uint32_t uploaded_sequence;
	int texture_ready;
	int capturable;
	VkBuffer capture;
	VkDeviceMemory capture_memory;
	void *capture_mapped;
	int capture_pending;
	uint32_t capture_frame;
	uint64_t capture_ns;
	int failed;
	swapchain_entry *next;
};

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static instance_entry instances[MAX_ITEMS];
static device_entry *devices[MAX_ITEMS];
static shm_header *shared;
static size_t shared_size;
static int shared_tried;
static int capture_available;

static void *dispatch_key(const void *handle)
{
	return *(void **)handle;
}

static instance_entry *find_instance(void *key)
{
	for (int i = 0; i < MAX_ITEMS; i++)
		if (instances[i].key == key)
			return &instances[i];
	return NULL;
}

static device_entry *find_device(void *key)
{
	for (int i = 0; i < MAX_ITEMS; i++)
		if (devices[i] && devices[i]->key == key)
			return devices[i];
	return NULL;
}

static uint64_t now_ns(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec;
}

static void open_shared(void)
{
	if (shared || shared_tried)
		return;
	shared_tried = 1;
	const char *path = getenv("VOIDSTRAP_OVERLAY_SHM");
	if (!path || !*path)
		return;
	int fd = open(path, O_RDWR | O_CLOEXEC);
	if (fd < 0)
		return;
	size_t wanted = SHM_HEADER + 2ull * MAX_EDGE * MAX_EDGE * 4ull;
	off_t length = lseek(fd, 0, SEEK_END);
	if (length < 0 || (size_t)length < wanted) {
		close(fd);
		return;
	}
	if ((size_t)length >= wanted + (size_t)MAX_EDGE * MAX_EDGE * 4u) {
		wanted += (size_t)MAX_EDGE * MAX_EDGE * 4u;
		capture_available = 1;
	}
	void *mapped = mmap(NULL, wanted, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
	close(fd);
	if (mapped == MAP_FAILED)
		return;
	shm_header *header = mapped;
	if (header->magic != SHM_MAGIC || header->version != SHM_VERSION) {
		munmap(mapped, wanted);
		return;
	}
	shared = header;
	shared_size = wanted;
}

static int memory_type(device_entry *device, uint32_t bits, VkMemoryPropertyFlags flags)
{
	for (uint32_t i = 0; i < device->memory.memoryTypeCount; i++)
		if ((bits & (1u << i)) && (device->memory.memoryTypes[i].propertyFlags & flags) == flags)
			return (int)i;
	return -1;
}

static void destroy_texture(swapchain_entry *chain)
{
	device_entry *d = chain->device;
	if (chain->texture_view)
		d->destroy_view(d->device, chain->texture_view, NULL);
	if (chain->texture)
		d->destroy_image(d->device, chain->texture, NULL);
	if (chain->texture_memory)
		d->free_memory(d->device, chain->texture_memory, NULL);
	if (chain->staging)
		d->destroy_buffer(d->device, chain->staging, NULL);
	if (chain->staging_memory)
		d->free_memory(d->device, chain->staging_memory, NULL);
	chain->texture_view = VK_NULL_HANDLE;
	chain->texture = VK_NULL_HANDLE;
	chain->texture_memory = VK_NULL_HANDLE;
	chain->staging = VK_NULL_HANDLE;
	chain->staging_memory = VK_NULL_HANDLE;
	chain->staging_mapped = NULL;
	chain->texture_width = 0;
	chain->texture_height = 0;
	chain->texture_ready = 0;
}

static int create_texture(swapchain_entry *chain, uint32_t width, uint32_t height)
{
	device_entry *d = chain->device;
	destroy_texture(chain);
	VkImageCreateInfo image = {
		.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
		.imageType = VK_IMAGE_TYPE_2D,
		.format = VK_FORMAT_B8G8R8A8_UNORM,
		.extent = { width, height, 1 },
		.mipLevels = 1,
		.arrayLayers = 1,
		.samples = VK_SAMPLE_COUNT_1_BIT,
		.tiling = VK_IMAGE_TILING_OPTIMAL,
		.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
		.sharingMode = VK_SHARING_MODE_EXCLUSIVE,
		.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED
	};
	if (d->create_image(d->device, &image, NULL, &chain->texture) != VK_SUCCESS)
		return 0;
	VkMemoryRequirements requirements;
	d->image_requirements(d->device, chain->texture, &requirements);
	int type = memory_type(d, requirements.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
	if (type < 0)
		return 0;
	VkMemoryAllocateInfo allocate = { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, .allocationSize = requirements.size, .memoryTypeIndex = (uint32_t)type };
	if (d->allocate_memory(d->device, &allocate, NULL, &chain->texture_memory) != VK_SUCCESS)
		return 0;
	d->bind_image(d->device, chain->texture, chain->texture_memory, 0);
	VkImageViewCreateInfo view = {
		.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
		.image = chain->texture,
		.viewType = VK_IMAGE_VIEW_TYPE_2D,
		.format = VK_FORMAT_B8G8R8A8_UNORM,
		.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 }
	};
	if (d->create_view(d->device, &view, NULL, &chain->texture_view) != VK_SUCCESS)
		return 0;
	VkBufferCreateInfo buffer = {
		.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
		.size = (VkDeviceSize)width * height * 4,
		.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT,
		.sharingMode = VK_SHARING_MODE_EXCLUSIVE
	};
	if (d->create_buffer(d->device, &buffer, NULL, &chain->staging) != VK_SUCCESS)
		return 0;
	d->buffer_requirements(d->device, chain->staging, &requirements);
	type = memory_type(d, requirements.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
	if (type < 0)
		return 0;
	allocate.allocationSize = requirements.size;
	allocate.memoryTypeIndex = (uint32_t)type;
	if (d->allocate_memory(d->device, &allocate, NULL, &chain->staging_memory) != VK_SUCCESS)
		return 0;
	d->bind_buffer(d->device, chain->staging, chain->staging_memory, 0);
	if (d->map_memory(d->device, chain->staging_memory, 0, VK_WHOLE_SIZE, 0, &chain->staging_mapped) != VK_SUCCESS)
		return 0;
	VkDescriptorImageInfo descriptor = { chain->sampler, chain->texture_view, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL };
	VkWriteDescriptorSet write = {
		.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
		.dstSet = chain->descriptor_set,
		.descriptorCount = 1,
		.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER,
		.pImageInfo = &descriptor
	};
	d->update_sets(d->device, 1, &write, 0, NULL);
	chain->texture_width = width;
	chain->texture_height = height;
	chain->uploaded_sequence = 0xffffffffu;
	return 1;
}

static void destroy_capture(swapchain_entry *chain)
{
	device_entry *d = chain->device;
	if (chain->capture)
		d->destroy_buffer(d->device, chain->capture, NULL);
	if (chain->capture_memory)
		d->free_memory(d->device, chain->capture_memory, NULL);
	chain->capture = VK_NULL_HANDLE;
	chain->capture_memory = VK_NULL_HANDLE;
	chain->capture_mapped = NULL;
	chain->capture_pending = 0;
}

static int create_capture(swapchain_entry *chain)
{
	device_entry *d = chain->device;
	if (chain->capture)
		return 1;
	VkBufferCreateInfo buffer = {
		.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
		.size = (VkDeviceSize)chain->extent.width * chain->extent.height * 4,
		.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT,
		.sharingMode = VK_SHARING_MODE_EXCLUSIVE
	};
	if (d->create_buffer(d->device, &buffer, NULL, &chain->capture) != VK_SUCCESS)
		return 0;
	VkMemoryRequirements requirements;
	d->buffer_requirements(d->device, chain->capture, &requirements);
	int type = memory_type(d, requirements.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT | VK_MEMORY_PROPERTY_HOST_CACHED_BIT);
	if (type < 0)
		type = memory_type(d, requirements.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
	if (type < 0)
		goto fail;
	VkMemoryAllocateInfo allocate = { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, .allocationSize = requirements.size, .memoryTypeIndex = (uint32_t)type };
	if (d->allocate_memory(d->device, &allocate, NULL, &chain->capture_memory) != VK_SUCCESS)
		goto fail;
	d->bind_buffer(d->device, chain->capture, chain->capture_memory, 0);
	if (d->map_memory(d->device, chain->capture_memory, 0, VK_WHOLE_SIZE, 0, &chain->capture_mapped) != VK_SUCCESS)
		goto fail;
	return 1;
fail:
	destroy_capture(chain);
	return 0;
}

static uint32_t capture_format(VkFormat format)
{
	switch (format) {
	case VK_FORMAT_B8G8R8A8_UNORM:
	case VK_FORMAT_B8G8R8A8_SRGB:
		return 1;
	case VK_FORMAT_R8G8B8A8_UNORM:
	case VK_FORMAT_R8G8B8A8_SRGB:
		return 2;
	default:
		return 0;
	}
}

static void finish_capture(swapchain_entry *chain)
{
	device_entry *d = chain->device;
	if (!chain->capture_pending || chain->capture_frame >= chain->count)
		return;
	frame_entry *frame = &chain->frames[chain->capture_frame];
	if (d->fence_status(d->device, frame->fence) != VK_SUCCESS)
		return;
	chain->capture_pending = 0;
	uint32_t width = chain->extent.width, height = chain->extent.height;
	if (!shared || width > MAX_EDGE || height > MAX_EDGE)
		return;
	uint8_t *target = (uint8_t *)shared + SHM_HEADER + 2ull * MAX_EDGE * MAX_EDGE * 4ull;
	memcpy(target, chain->capture_mapped, (size_t)width * height * 4u);
	shared->capture_format = capture_format(chain->format);
	shared->swap_width = width;
	shared->swap_height = height;
	__atomic_thread_fence(__ATOMIC_RELEASE);
	__atomic_add_fetch(&shared->capture_sequence, 1, __ATOMIC_RELAXED);
}

static int wants_capture(swapchain_entry *chain)
{
	if (!shared || !capture_available || !chain->capturable || !shared->capture_wanted || chain->capture_pending)
		return 0;
	if (!capture_format(chain->format) || chain->extent.width > MAX_EDGE || chain->extent.height > MAX_EDGE)
		return 0;
	uint64_t now = now_ns();
	if (now - chain->capture_ns < 33000000ull)
		return 0;
	if (!create_capture(chain))
		return 0;
	chain->capture_ns = now;
	return 1;
}

static void record_capture(device_entry *d, swapchain_entry *chain, frame_entry *frame, VkCommandBuffer commands)
{
	VkImageMemoryBarrier to_source = {
		.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
		.srcAccessMask = VK_ACCESS_MEMORY_WRITE_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
		.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
		.oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
		.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
		.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
		.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
		.image = frame->image,
		.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 }
	};
	d->cmd_barrier(commands, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, NULL, 0, NULL, 1, &to_source);
	VkBufferImageCopy region = {
		.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
		.imageExtent = { chain->extent.width, chain->extent.height, 1 }
	};
	d->cmd_copy_out(commands, frame->image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, chain->capture, 1, &region);
	VkImageMemoryBarrier to_present = to_source;
	to_present.srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
	to_present.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
	to_present.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
	to_present.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
	d->cmd_barrier(commands, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, 0, 0, NULL, 0, NULL, 1, &to_present);
	VkBufferMemoryBarrier to_host = {
		.sType = VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER,
		.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
		.dstAccessMask = VK_ACCESS_HOST_READ_BIT,
		.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
		.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
		.buffer = chain->capture,
		.size = VK_WHOLE_SIZE
	};
	d->cmd_barrier(commands, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 0, NULL, 1, &to_host, 0, NULL);
}

static void destroy_chain_resources(swapchain_entry *chain)
{
	device_entry *d = chain->device;
	if (!d)
		return;
	d->wait_idle(d->device);
	destroy_capture(chain);
	for (uint32_t i = 0; i < chain->count && chain->frames; i++) {
		frame_entry *frame = &chain->frames[i];
		if (frame->framebuffer)
			d->destroy_framebuffer(d->device, frame->framebuffer, NULL);
		if (frame->view)
			d->destroy_view(d->device, frame->view, NULL);
		if (frame->fence)
			d->destroy_fence(d->device, frame->fence, NULL);
		if (frame->finished)
			d->destroy_semaphore(d->device, frame->finished, NULL);
	}
	free(chain->frames);
	chain->frames = NULL;
	destroy_texture(chain);
	if (chain->command_pool)
		d->destroy_command_pool(d->device, chain->command_pool, NULL);
	if (chain->pipeline)
		d->destroy_pipeline(d->device, chain->pipeline, NULL);
	if (chain->pipeline_layout)
		d->destroy_pipeline_layout(d->device, chain->pipeline_layout, NULL);
	if (chain->descriptor_pool)
		d->destroy_descriptor_pool(d->device, chain->descriptor_pool, NULL);
	if (chain->set_layout)
		d->destroy_set_layout(d->device, chain->set_layout, NULL);
	if (chain->sampler)
		d->destroy_sampler(d->device, chain->sampler, NULL);
	if (chain->render_pass)
		d->destroy_render_pass(d->device, chain->render_pass, NULL);
}

static int build_chain(swapchain_entry *chain)
{
	device_entry *d = chain->device;
	if (!d->has_graphics || !d->graphics_queue)
		return 0;
	if (d->get_images(d->device, chain->handle, &chain->count, NULL) != VK_SUCCESS || chain->count == 0)
		return 0;
	VkImage *images = calloc(chain->count, sizeof(VkImage));
	chain->frames = calloc(chain->count, sizeof(frame_entry));
	if (!images || !chain->frames) {
		free(images);
		return 0;
	}
	d->get_images(d->device, chain->handle, &chain->count, images);

	VkAttachmentDescription attachment = {
		.format = chain->format,
		.samples = VK_SAMPLE_COUNT_1_BIT,
		.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD,
		.storeOp = VK_ATTACHMENT_STORE_OP_STORE,
		.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE,
		.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE,
		.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
		.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR
	};
	VkAttachmentReference reference = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
	VkSubpassDescription subpass = { .pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS, .colorAttachmentCount = 1, .pColorAttachments = &reference };
	VkSubpassDependency dependency = {
		.srcSubpass = VK_SUBPASS_EXTERNAL,
		.dstSubpass = 0,
		.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
		.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
		.srcAccessMask = 0,
		.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT
	};
	VkRenderPassCreateInfo pass = {
		.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO,
		.attachmentCount = 1,
		.pAttachments = &attachment,
		.subpassCount = 1,
		.pSubpasses = &subpass,
		.dependencyCount = 1,
		.pDependencies = &dependency
	};
	if (d->create_render_pass(d->device, &pass, NULL, &chain->render_pass) != VK_SUCCESS)
		goto fail;

	VkSamplerCreateInfo sampler = {
		.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
		.magFilter = VK_FILTER_LINEAR,
		.minFilter = VK_FILTER_LINEAR,
		.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
		.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
		.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
		.maxLod = 0.0f
	};
	if (d->create_sampler(d->device, &sampler, NULL, &chain->sampler) != VK_SUCCESS)
		goto fail;

	VkDescriptorSetLayoutBinding binding = { 0, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1, VK_SHADER_STAGE_FRAGMENT_BIT, NULL };
	VkDescriptorSetLayoutCreateInfo set_layout = { .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO, .bindingCount = 1, .pBindings = &binding };
	if (d->create_set_layout(d->device, &set_layout, NULL, &chain->set_layout) != VK_SUCCESS)
		goto fail;
	VkDescriptorPoolSize size = { VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1 };
	VkDescriptorPoolCreateInfo pool = { .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO, .maxSets = 1, .poolSizeCount = 1, .pPoolSizes = &size };
	if (d->create_descriptor_pool(d->device, &pool, NULL, &chain->descriptor_pool) != VK_SUCCESS)
		goto fail;
	VkDescriptorSetAllocateInfo set = { .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO, .descriptorPool = chain->descriptor_pool, .descriptorSetCount = 1, .pSetLayouts = &chain->set_layout };
	if (d->allocate_sets(d->device, &set, &chain->descriptor_set) != VK_SUCCESS)
		goto fail;
	VkPipelineLayoutCreateInfo layout = { .sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, .setLayoutCount = 1, .pSetLayouts = &chain->set_layout };
	if (d->create_pipeline_layout(d->device, &layout, NULL, &chain->pipeline_layout) != VK_SUCCESS)
		goto fail;

	VkShaderModuleCreateInfo vertex_info = { .sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, .codeSize = sizeof(vertex_spirv), .pCode = vertex_spirv };
	VkShaderModuleCreateInfo fragment_info = { .sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, .codeSize = sizeof(fragment_spirv), .pCode = fragment_spirv };
	VkShaderModule vertex = VK_NULL_HANDLE, fragment = VK_NULL_HANDLE;
	d->create_shader(d->device, &vertex_info, NULL, &vertex);
	d->create_shader(d->device, &fragment_info, NULL, &fragment);
	VkPipelineShaderStageCreateInfo stages[2] = {
		{ .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, .stage = VK_SHADER_STAGE_VERTEX_BIT, .module = vertex, .pName = "main" },
		{ .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, .stage = VK_SHADER_STAGE_FRAGMENT_BIT, .module = fragment, .pName = "main" }
	};
	VkPipelineVertexInputStateCreateInfo input = { .sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
	VkPipelineInputAssemblyStateCreateInfo assembly = { .sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO, .topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST };
	VkPipelineViewportStateCreateInfo viewport = { .sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO, .viewportCount = 1, .scissorCount = 1 };
	VkPipelineRasterizationStateCreateInfo raster = { .sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO, .polygonMode = VK_POLYGON_MODE_FILL, .cullMode = VK_CULL_MODE_NONE, .frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE, .lineWidth = 1.0f };
	VkPipelineMultisampleStateCreateInfo multisample = { .sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO, .rasterizationSamples = VK_SAMPLE_COUNT_1_BIT };
	VkPipelineColorBlendAttachmentState blend_attachment = {
		.blendEnable = VK_TRUE,
		.srcColorBlendFactor = VK_BLEND_FACTOR_ONE,
		.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA,
		.colorBlendOp = VK_BLEND_OP_ADD,
		.srcAlphaBlendFactor = VK_BLEND_FACTOR_ZERO,
		.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE,
		.alphaBlendOp = VK_BLEND_OP_ADD,
		.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT
	};
	VkPipelineColorBlendStateCreateInfo blend = { .sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO, .attachmentCount = 1, .pAttachments = &blend_attachment };
	VkDynamicState dynamic_states[2] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
	VkPipelineDynamicStateCreateInfo dynamic = { .sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO, .dynamicStateCount = 2, .pDynamicStates = dynamic_states };
	VkGraphicsPipelineCreateInfo pipeline = {
		.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO,
		.stageCount = 2,
		.pStages = stages,
		.pVertexInputState = &input,
		.pInputAssemblyState = &assembly,
		.pViewportState = &viewport,
		.pRasterizationState = &raster,
		.pMultisampleState = &multisample,
		.pColorBlendState = &blend,
		.pDynamicState = &dynamic,
		.layout = chain->pipeline_layout,
		.renderPass = chain->render_pass
	};
	VkResult built = vertex && fragment ? d->create_pipelines(d->device, VK_NULL_HANDLE, 1, &pipeline, NULL, &chain->pipeline) : VK_ERROR_INITIALIZATION_FAILED;
	if (vertex)
		d->destroy_shader(d->device, vertex, NULL);
	if (fragment)
		d->destroy_shader(d->device, fragment, NULL);
	if (built != VK_SUCCESS)
		goto fail;

	VkCommandPoolCreateInfo command_pool = { .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, .queueFamilyIndex = d->graphics_family };
	if (d->create_command_pool(d->device, &command_pool, NULL, &chain->command_pool) != VK_SUCCESS)
		goto fail;

	for (uint32_t i = 0; i < chain->count; i++) {
		frame_entry *frame = &chain->frames[i];
		frame->image = images[i];
		VkImageViewCreateInfo view = {
			.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
			.image = images[i],
			.viewType = VK_IMAGE_VIEW_TYPE_2D,
			.format = chain->format,
			.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 }
		};
		if (d->create_view(d->device, &view, NULL, &frame->view) != VK_SUCCESS)
			goto fail;
		VkFramebufferCreateInfo framebuffer = {
			.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO,
			.renderPass = chain->render_pass,
			.attachmentCount = 1,
			.pAttachments = &frame->view,
			.width = chain->extent.width,
			.height = chain->extent.height,
			.layers = 1
		};
		if (d->create_framebuffer(d->device, &framebuffer, NULL, &frame->framebuffer) != VK_SUCCESS)
			goto fail;
		VkCommandBufferAllocateInfo commands = { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, .commandPool = chain->command_pool, .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY, .commandBufferCount = 1 };
		if (d->allocate_commands(d->device, &commands, &frame->commands) != VK_SUCCESS)
			goto fail;
		if (d->set_loader_data)
			d->set_loader_data(d->device, frame->commands);
		else
			*(void **)frame->commands = *(void **)d->device;
		VkFenceCreateInfo fence = { .sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO, .flags = VK_FENCE_CREATE_SIGNALED_BIT };
		VkSemaphoreCreateInfo semaphore = { .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
		if (d->create_fence(d->device, &fence, NULL, &frame->fence) != VK_SUCCESS
			|| d->create_semaphore(d->device, &semaphore, NULL, &frame->finished) != VK_SUCCESS)
			goto fail;
	}
	free(images);
	return 1;
fail:
	free(images);
	return 0;
}

static void record_upload(device_entry *d, swapchain_entry *chain, VkCommandBuffer commands)
{
	VkImageMemoryBarrier to_transfer = {
		.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
		.srcAccessMask = 0,
		.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
		.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED,
		.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
		.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
		.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
		.image = chain->texture,
		.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 }
	};
	d->cmd_barrier(commands, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, NULL, 0, NULL, 1, &to_transfer);
	VkBufferImageCopy region = {
		.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 },
		.imageExtent = { chain->texture_width, chain->texture_height, 1 }
	};
	d->cmd_copy(commands, chain->staging, chain->texture, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
	VkImageMemoryBarrier to_shader = to_transfer;
	to_shader.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
	to_shader.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
	to_shader.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
	to_shader.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
	d->cmd_barrier(commands, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, NULL, 0, NULL, 1, &to_shader);
}

static int prepare_overlay(swapchain_entry *chain, int *needs_upload)
{
	*needs_upload = 0;
	open_shared();
	if (!shared)
		return 0;
	shared->swap_width = chain->extent.width;
	shared->swap_height = chain->extent.height;
	if (!shared->visible)
		return 0;
	uint32_t sequence = shared->sequence;
	__atomic_thread_fence(__ATOMIC_ACQUIRE);
	uint32_t width = shared->width;
	uint32_t height = shared->height;
	uint32_t slot = shared->slot & 1u;
	if (width == 0 || height == 0 || width > MAX_EDGE || height > MAX_EDGE)
		return 0;
	if (width != chain->texture_width || height != chain->texture_height) {
		if (!create_texture(chain, width, height))
			return 0;
	}
	if (sequence != chain->uploaded_sequence) {
		const uint8_t *pixels = (const uint8_t *)shared + SHM_HEADER + (size_t)slot * MAX_EDGE * MAX_EDGE * 4u;
		memcpy(chain->staging_mapped, pixels, (size_t)width * height * 4u);
		chain->uploaded_sequence = sequence;
		*needs_upload = 1;
	}
	return chain->texture_ready || *needs_upload;
}

static VkResult VKAPI_CALL layer_QueuePresentKHR(VkQueue queue, const VkPresentInfoKHR *info)
{
	device_entry *d;
	pthread_mutex_lock(&lock);
	d = find_device(dispatch_key(queue));
	pthread_mutex_unlock(&lock);
	if (!d)
		return VK_ERROR_DEVICE_LOST;
	if (shared) {
		__atomic_add_fetch(&shared->presented, 1, __ATOMIC_RELAXED);
		shared->present_ns = now_ns();
	}
	int graphics_queue = 0;
	pthread_mutex_lock(&lock);
	for (uint32_t i = 0; i < d->queue_count; i++)
		if (d->queues[i] == queue)
			graphics_queue = d->queue_families[i] == d->graphics_family;
	pthread_mutex_unlock(&lock);
	if (!info || info->swapchainCount != 1 || !d->has_graphics || !graphics_queue)
		return d->present(queue, info);

	pthread_mutex_lock(&lock);
	swapchain_entry *chain = d->swapchains;
	while (chain && chain->handle != info->pSwapchains[0])
		chain = chain->next;
	int upload = 0;
	int usable = chain && !chain->failed && info->pImageIndices[0] < chain->count;
	if (usable)
		finish_capture(chain);
	int draw = usable && prepare_overlay(chain, &upload);
	frame_entry *frame = usable ? &chain->frames[info->pImageIndices[0]] : NULL;
	int capture = usable && wants_capture(chain);
	if (!draw && !capture) {
		pthread_mutex_unlock(&lock);
		return d->present(queue, info);
	}
	if (frame->submitted)
		d->wait_fences(d->device, 1, &frame->fence, VK_TRUE, UINT64_MAX);
	d->reset_fences(d->device, 1, &frame->fence);
	d->reset_commands(frame->commands, 0);
	VkCommandBufferBeginInfo begin = { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO, .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
	d->begin_commands(frame->commands, &begin);
	if (upload)
		record_upload(d, chain, frame->commands);
	if (capture)
		record_capture(d, chain, frame, frame->commands);
	if (draw) {
		VkRenderPassBeginInfo pass = {
			.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO,
			.renderPass = chain->render_pass,
			.framebuffer = frame->framebuffer,
			.renderArea = { { 0, 0 }, chain->extent }
		};
		d->cmd_begin_pass(frame->commands, &pass, VK_SUBPASS_CONTENTS_INLINE);
		VkViewport viewport = { 0.0f, 0.0f, (float)chain->extent.width, (float)chain->extent.height, 0.0f, 1.0f };
		VkRect2D scissor = { { 0, 0 }, chain->extent };
		d->cmd_viewport(frame->commands, 0, 1, &viewport);
		d->cmd_scissor(frame->commands, 0, 1, &scissor);
		d->cmd_bind_pipeline(frame->commands, VK_PIPELINE_BIND_POINT_GRAPHICS, chain->pipeline);
		d->cmd_bind_sets(frame->commands, VK_PIPELINE_BIND_POINT_GRAPHICS, chain->pipeline_layout, 0, 1, &chain->descriptor_set, 0, NULL);
		d->cmd_draw(frame->commands, 3, 1, 0, 0);
		d->cmd_end_pass(frame->commands);
	}
	d->end_commands(frame->commands);

	uint32_t waits = info->waitSemaphoreCount;
	VkPipelineStageFlags *stages = NULL;
	if (waits) {
		stages = malloc(sizeof(VkPipelineStageFlags) * waits);
		for (uint32_t i = 0; stages && i < waits; i++)
			stages[i] = capture ? VK_PIPELINE_STAGE_ALL_COMMANDS_BIT : VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
	}
	VkSubmitInfo submit = {
		.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
		.waitSemaphoreCount = stages ? waits : 0,
		.pWaitSemaphores = info->pWaitSemaphores,
		.pWaitDstStageMask = stages,
		.commandBufferCount = 1,
		.pCommandBuffers = &frame->commands,
		.signalSemaphoreCount = 1,
		.pSignalSemaphores = &frame->finished
	};
	VkResult submitted = d->submit(queue, 1, &submit, frame->fence);
	free(stages);
	if (submitted != VK_SUCCESS) {
		chain->failed = 1;
		pthread_mutex_unlock(&lock);
		return d->present(queue, info);
	}
	frame->submitted = 1;
	if (upload)
		chain->texture_ready = 1;
	if (capture) {
		chain->capture_pending = 1;
		chain->capture_frame = info->pImageIndices[0];
	}
	VkPresentInfoKHR replaced = *info;
	replaced.waitSemaphoreCount = 1;
	replaced.pWaitSemaphores = &frame->finished;
	pthread_mutex_unlock(&lock);
	return d->present(queue, &replaced);
}

static void remember_queue(device_entry *d, uint32_t family, VkQueue queue)
{
	if (!queue)
		return;
	pthread_mutex_lock(&lock);
	for (uint32_t i = 0; i < d->queue_count; i++) {
		if (d->queues[i] == queue) {
			pthread_mutex_unlock(&lock);
			return;
		}
	}
	if (d->queue_count < 16) {
		d->queues[d->queue_count] = queue;
		d->queue_families[d->queue_count] = family;
		d->queue_count++;
	}
	pthread_mutex_unlock(&lock);
}

static void VKAPI_CALL layer_GetDeviceQueue(VkDevice device, uint32_t family, uint32_t index, VkQueue *queue)
{
	pthread_mutex_lock(&lock);
	device_entry *d = find_device(dispatch_key(device));
	pthread_mutex_unlock(&lock);
	if (!d)
		return;
	d->get_queue(device, family, index, queue);
	remember_queue(d, family, *queue);
}

static void VKAPI_CALL layer_GetDeviceQueue2(VkDevice device, const VkDeviceQueueInfo2 *info, VkQueue *queue)
{
	pthread_mutex_lock(&lock);
	device_entry *d = find_device(dispatch_key(device));
	pthread_mutex_unlock(&lock);
	if (!d || !d->get_queue2)
		return;
	d->get_queue2(device, info, queue);
	remember_queue(d, info->queueFamilyIndex, *queue);
}

static VkResult VKAPI_CALL layer_CreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR *info, const VkAllocationCallbacks *allocator, VkSwapchainKHR *swapchain)
{
	pthread_mutex_lock(&lock);
	device_entry *d = find_device(dispatch_key(device));
	pthread_mutex_unlock(&lock);
	if (!d)
		return VK_ERROR_INITIALIZATION_FAILED;
	VkSwapchainCreateInfoKHR modified = *info;
	modified.imageUsage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
	VkResult result = d->create_swapchain(device, &modified, allocator, swapchain);
	int capturable = result == VK_SUCCESS;
	if (result != VK_SUCCESS) {
		modified.imageUsage = info->imageUsage | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
		result = d->create_swapchain(device, &modified, allocator, swapchain);
	}
	if (result != VK_SUCCESS)
		return result;
	swapchain_entry *chain = calloc(1, sizeof(swapchain_entry));
	if (!chain)
		return result;
	chain->handle = *swapchain;
	chain->device = d;
	chain->format = info->imageFormat;
	chain->extent = info->imageExtent;
	chain->capturable = capturable;
	if (!build_chain(chain)) {
		destroy_chain_resources(chain);
		chain->failed = 1;
	}
	pthread_mutex_lock(&lock);
	chain->next = d->swapchains;
	d->swapchains = chain;
	pthread_mutex_unlock(&lock);
	return result;
}

static void VKAPI_CALL layer_DestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks *allocator)
{
	pthread_mutex_lock(&lock);
	device_entry *d = find_device(dispatch_key(device));
	swapchain_entry **link = d ? &d->swapchains : NULL;
	swapchain_entry *found = NULL;
	while (link && *link) {
		if ((*link)->handle == swapchain) {
			found = *link;
			*link = found->next;
			break;
		}
		link = &(*link)->next;
	}
	pthread_mutex_unlock(&lock);
	if (found) {
		destroy_chain_resources(found);
		free(found);
	}
	if (d)
		d->destroy_swapchain(device, swapchain, allocator);
}

#define LOAD(field, name) d->field = (void *)d->gdpa(*device, name)

static VkResult VKAPI_CALL layer_CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo *info, const VkAllocationCallbacks *allocator, VkDevice *device)
{
	VkLayerDeviceCreateInfo *chain = (VkLayerDeviceCreateInfo *)info->pNext;
	while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && chain->function == VK_LAYER_LINK_INFO))
		chain = (VkLayerDeviceCreateInfo *)chain->pNext;
	if (!chain)
		return VK_ERROR_INITIALIZATION_FAILED;
	PFN_vkGetInstanceProcAddr gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
	PFN_vkGetDeviceProcAddr gdpa = chain->u.pLayerInfo->pfnNextGetDeviceProcAddr;
	chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;
	PFN_vkSetDeviceLoaderData set_loader_data = NULL;
	for (VkLayerDeviceCreateInfo *callback = (VkLayerDeviceCreateInfo *)info->pNext; callback; callback = (VkLayerDeviceCreateInfo *)callback->pNext)
		if (callback->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && callback->function == VK_LOADER_DATA_CALLBACK)
			set_loader_data = callback->u.pfnSetDeviceLoaderData;
	PFN_vkCreateDevice create = (PFN_vkCreateDevice)gipa(VK_NULL_HANDLE, "vkCreateDevice");
	VkResult result = create(physical, info, allocator, device);
	if (result != VK_SUCCESS)
		return result;

	device_entry *d = calloc(1, sizeof(device_entry));
	if (!d)
		return result;
	d->key = dispatch_key(*device);
	d->device = *device;
	d->physical = physical;
	d->gdpa = gdpa;
	d->set_loader_data = set_loader_data;
	pthread_mutex_lock(&lock);
	instance_entry *instance = find_instance(dispatch_key(physical));
	pthread_mutex_unlock(&lock);
	if (instance) {
		instance->memory_properties(physical, &d->memory);
		uint32_t families = 0;
		instance->queue_families(physical, &families, NULL);
		VkQueueFamilyProperties *properties = calloc(families ? families : 1, sizeof(VkQueueFamilyProperties));
		if (properties)
			instance->queue_families(physical, &families, properties);
		for (uint32_t i = 0; properties && i < info->queueCreateInfoCount && !d->has_graphics; i++) {
			uint32_t family = info->pQueueCreateInfos[i].queueFamilyIndex;
			if (family < families && (properties[family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) {
				d->graphics_family = family;
				d->has_graphics = 1;
			}
		}
		free(properties);
	}
	LOAD(destroy_device, "vkDestroyDevice");
	LOAD(get_queue, "vkGetDeviceQueue");
	LOAD(get_queue2, "vkGetDeviceQueue2");
	LOAD(create_swapchain, "vkCreateSwapchainKHR");
	LOAD(destroy_swapchain, "vkDestroySwapchainKHR");
	LOAD(get_images, "vkGetSwapchainImagesKHR");
	LOAD(present, "vkQueuePresentKHR");
	LOAD(submit, "vkQueueSubmit");
	LOAD(create_view, "vkCreateImageView");
	LOAD(destroy_view, "vkDestroyImageView");
	LOAD(create_render_pass, "vkCreateRenderPass");
	LOAD(destroy_render_pass, "vkDestroyRenderPass");
	LOAD(create_framebuffer, "vkCreateFramebuffer");
	LOAD(destroy_framebuffer, "vkDestroyFramebuffer");
	LOAD(create_shader, "vkCreateShaderModule");
	LOAD(destroy_shader, "vkDestroyShaderModule");
	LOAD(create_pipeline_layout, "vkCreatePipelineLayout");
	LOAD(destroy_pipeline_layout, "vkDestroyPipelineLayout");
	LOAD(create_pipelines, "vkCreateGraphicsPipelines");
	LOAD(destroy_pipeline, "vkDestroyPipeline");
	LOAD(create_set_layout, "vkCreateDescriptorSetLayout");
	LOAD(destroy_set_layout, "vkDestroyDescriptorSetLayout");
	LOAD(create_descriptor_pool, "vkCreateDescriptorPool");
	LOAD(destroy_descriptor_pool, "vkDestroyDescriptorPool");
	LOAD(allocate_sets, "vkAllocateDescriptorSets");
	LOAD(update_sets, "vkUpdateDescriptorSets");
	LOAD(create_sampler, "vkCreateSampler");
	LOAD(destroy_sampler, "vkDestroySampler");
	LOAD(create_command_pool, "vkCreateCommandPool");
	LOAD(destroy_command_pool, "vkDestroyCommandPool");
	LOAD(allocate_commands, "vkAllocateCommandBuffers");
	LOAD(begin_commands, "vkBeginCommandBuffer");
	LOAD(end_commands, "vkEndCommandBuffer");
	LOAD(reset_commands, "vkResetCommandBuffer");
	LOAD(cmd_begin_pass, "vkCmdBeginRenderPass");
	LOAD(cmd_end_pass, "vkCmdEndRenderPass");
	LOAD(cmd_bind_pipeline, "vkCmdBindPipeline");
	LOAD(cmd_bind_sets, "vkCmdBindDescriptorSets");
	LOAD(cmd_draw, "vkCmdDraw");
	LOAD(cmd_viewport, "vkCmdSetViewport");
	LOAD(cmd_scissor, "vkCmdSetScissor");
	LOAD(cmd_barrier, "vkCmdPipelineBarrier");
	LOAD(cmd_copy, "vkCmdCopyBufferToImage");
	LOAD(cmd_copy_out, "vkCmdCopyImageToBuffer");
	LOAD(fence_status, "vkGetFenceStatus");
	LOAD(create_fence, "vkCreateFence");
	LOAD(destroy_fence, "vkDestroyFence");
	LOAD(wait_fences, "vkWaitForFences");
	LOAD(reset_fences, "vkResetFences");
	LOAD(create_semaphore, "vkCreateSemaphore");
	LOAD(destroy_semaphore, "vkDestroySemaphore");
	LOAD(create_image, "vkCreateImage");
	LOAD(destroy_image, "vkDestroyImage");
	LOAD(create_buffer, "vkCreateBuffer");
	LOAD(destroy_buffer, "vkDestroyBuffer");
	LOAD(image_requirements, "vkGetImageMemoryRequirements");
	LOAD(buffer_requirements, "vkGetBufferMemoryRequirements");
	LOAD(allocate_memory, "vkAllocateMemory");
	LOAD(free_memory, "vkFreeMemory");
	LOAD(bind_image, "vkBindImageMemory");
	LOAD(bind_buffer, "vkBindBufferMemory");
	LOAD(map_memory, "vkMapMemory");
	LOAD(wait_idle, "vkDeviceWaitIdle");
	if (d->has_graphics && d->get_queue) {
		d->get_queue(*device, d->graphics_family, 0, &d->graphics_queue);
		if (d->graphics_queue && d->set_loader_data)
			d->set_loader_data(*device, d->graphics_queue);
	}

	pthread_mutex_lock(&lock);
	for (int i = 0; i < MAX_ITEMS; i++) {
		if (!devices[i]) {
			devices[i] = d;
			d = NULL;
			break;
		}
	}
	pthread_mutex_unlock(&lock);
	free(d);
	return result;
}

static void VKAPI_CALL layer_DestroyDevice(VkDevice device, const VkAllocationCallbacks *allocator)
{
	pthread_mutex_lock(&lock);
	device_entry *d = NULL;
	for (int i = 0; i < MAX_ITEMS; i++) {
		if (devices[i] && devices[i]->key == dispatch_key(device)) {
			d = devices[i];
			devices[i] = NULL;
			break;
		}
	}
	pthread_mutex_unlock(&lock);
	if (!d)
		return;
	while (d->swapchains) {
		swapchain_entry *chain = d->swapchains;
		d->swapchains = chain->next;
		destroy_chain_resources(chain);
		free(chain);
	}
	PFN_vkDestroyDevice destroy = d->destroy_device;
	free(d);
	destroy(device, allocator);
}

static VkResult VKAPI_CALL layer_CreateInstance(const VkInstanceCreateInfo *info, const VkAllocationCallbacks *allocator, VkInstance *instance)
{
	VkLayerInstanceCreateInfo *chain = (VkLayerInstanceCreateInfo *)info->pNext;
	while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && chain->function == VK_LAYER_LINK_INFO))
		chain = (VkLayerInstanceCreateInfo *)chain->pNext;
	if (!chain)
		return VK_ERROR_INITIALIZATION_FAILED;
	PFN_vkGetInstanceProcAddr gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
	chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;
	PFN_vkCreateInstance create = (PFN_vkCreateInstance)gipa(VK_NULL_HANDLE, "vkCreateInstance");
	VkResult result = create(info, allocator, instance);
	if (result != VK_SUCCESS)
		return result;
	pthread_mutex_lock(&lock);
	for (int i = 0; i < MAX_ITEMS; i++) {
		if (!instances[i].key) {
			instances[i].key = dispatch_key(*instance);
			instances[i].instance = *instance;
			instances[i].gipa = gipa;
			instances[i].destroy = (PFN_vkDestroyInstance)gipa(*instance, "vkDestroyInstance");
			instances[i].memory_properties = (PFN_vkGetPhysicalDeviceMemoryProperties)gipa(*instance, "vkGetPhysicalDeviceMemoryProperties");
			instances[i].queue_families = (PFN_vkGetPhysicalDeviceQueueFamilyProperties)gipa(*instance, "vkGetPhysicalDeviceQueueFamilyProperties");
			break;
		}
	}
	pthread_mutex_unlock(&lock);
	return result;
}

static void VKAPI_CALL layer_DestroyInstance(VkInstance instance, const VkAllocationCallbacks *allocator)
{
	pthread_mutex_lock(&lock);
	instance_entry *entry = find_instance(dispatch_key(instance));
	PFN_vkDestroyInstance destroy = entry ? entry->destroy : NULL;
	if (entry)
		memset(entry, 0, sizeof(*entry));
	pthread_mutex_unlock(&lock);
	if (destroy)
		destroy(instance, allocator);
}

EXPORT PFN_vkVoidFunction VKAPI_CALL voidstrap_overlay_GetDeviceProcAddr(VkDevice device, const char *name);

static PFN_vkVoidFunction device_hook(const char *name)
{
	if (!strcmp(name, "vkGetDeviceProcAddr"))
		return (PFN_vkVoidFunction)voidstrap_overlay_GetDeviceProcAddr;
	if (!strcmp(name, "vkDestroyDevice"))
		return (PFN_vkVoidFunction)layer_DestroyDevice;
	if (!strcmp(name, "vkCreateSwapchainKHR"))
		return (PFN_vkVoidFunction)layer_CreateSwapchainKHR;
	if (!strcmp(name, "vkDestroySwapchainKHR"))
		return (PFN_vkVoidFunction)layer_DestroySwapchainKHR;
	if (!strcmp(name, "vkQueuePresentKHR"))
		return (PFN_vkVoidFunction)layer_QueuePresentKHR;
	if (!strcmp(name, "vkGetDeviceQueue"))
		return (PFN_vkVoidFunction)layer_GetDeviceQueue;
	if (!strcmp(name, "vkGetDeviceQueue2"))
		return (PFN_vkVoidFunction)layer_GetDeviceQueue2;
	return NULL;
}

EXPORT PFN_vkVoidFunction VKAPI_CALL voidstrap_overlay_GetDeviceProcAddr(VkDevice device, const char *name)
{
	PFN_vkVoidFunction hook = device_hook(name);
	if (hook)
		return hook;
	pthread_mutex_lock(&lock);
	device_entry *d = find_device(dispatch_key(device));
	pthread_mutex_unlock(&lock);
	return d ? d->gdpa(device, name) : NULL;
}

EXPORT PFN_vkVoidFunction VKAPI_CALL voidstrap_overlay_GetInstanceProcAddr(VkInstance instance, const char *name)
{
	if (!strcmp(name, "vkGetInstanceProcAddr"))
		return (PFN_vkVoidFunction)voidstrap_overlay_GetInstanceProcAddr;
	if (!strcmp(name, "vkCreateInstance"))
		return (PFN_vkVoidFunction)layer_CreateInstance;
	if (!strcmp(name, "vkDestroyInstance"))
		return (PFN_vkVoidFunction)layer_DestroyInstance;
	if (!strcmp(name, "vkCreateDevice"))
		return (PFN_vkVoidFunction)layer_CreateDevice;
	PFN_vkVoidFunction hook = device_hook(name);
	if (hook)
		return hook;
	if (!instance)
		return NULL;
	pthread_mutex_lock(&lock);
	instance_entry *entry = find_instance(dispatch_key(instance));
	pthread_mutex_unlock(&lock);
	return entry ? entry->gipa(instance, name) : NULL;
}

EXPORT VkResult VKAPI_CALL voidstrap_overlay_NegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface *version)
{
	if (!version || version->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT)
		return VK_ERROR_INITIALIZATION_FAILED;
	if (version->loaderLayerInterfaceVersion > 2)
		version->loaderLayerInterfaceVersion = 2;
	version->pfnGetInstanceProcAddr = voidstrap_overlay_GetInstanceProcAddr;
	version->pfnGetDeviceProcAddr = voidstrap_overlay_GetDeviceProcAddr;
	version->pfnGetPhysicalDeviceProcAddr = NULL;
	return VK_SUCCESS;
}
